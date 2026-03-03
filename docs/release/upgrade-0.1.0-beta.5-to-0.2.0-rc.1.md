# Upgrade Validation: 0.1.0-beta.5 -> 0.2.0-rc.1

## Purpose

Validate that upgrading from `0.1.0-beta.5` to `0.2.0-rc.1` behaves as an in-place upgrade and does not break basic launch or local runtime data.

## Install Location

The installer targets:

`%LOCALAPPDATA%\Programs\nLink Helper`

This comes from `DefaultDirName={localappdata}\Programs\nLink Helper` in `installer/nLink.iss`.

## Upgrade Behavior

- The Inno `AppId` is stable across beta and RC builds.
- Expected result: installing `0.2.0-rc.1` over `0.1.0-beta.5` performs an in-place upgrade in the same install directory.
- Expected result: `nLink.exe` and the packaged `bridge\` payload are replaced with the RC build.

## Validation Steps

1. Install `0.1.0-beta.5`.
2. Launch the app once.
3. Create one minimal session flow and then close the app.
4. Confirm the install folder exists at `%LOCALAPPDATA%\Programs\nLink Helper`.
5. Install `0.2.0-rc.1` over the existing install.
6. Launch `0.2.0-rc.1`.
7. Verify the app starts normally and reaches the main UI.
8. Verify the install folder still contains:
   - `nLink.exe`
   - `appsettings.json`
   - `bridge\win-x64\`
9. Verify local data is still present, if it existed before upgrade:
   - logs under `%LOCALAPPDATA%\nLink\logs\`
   - reliability log at `%LOCALAPPDATA%\nLink\reliability.jsonl`
   - NKN identity at `%LOCALAPPDATA%\nLink\identity.json`
10. If the beta build created runtime artifacts, verify they remain readable after upgrade.

## Uninstall Expectations

- Silent or interactive uninstall should remove the installed app payload from `%LOCALAPPDATA%\Programs\nLink Helper`.
- The uninstall should remove `nLink.exe`.
- It is acceptable if the install directory remains temporarily or ends up empty after uninstall.
- User data under `%LOCALAPPDATA%\nLink\` may remain unless explicitly removed by the app or uninstaller.

## Rollback

1. Uninstall `0.2.0-rc.1`.
2. Reinstall `0.1.0-beta.5`.
3. Launch once and verify the app still starts from `%LOCALAPPDATA%\Programs\nLink Helper`.
4. Re-check local data under `%LOCALAPPDATA%\nLink\` if rollback preservation matters for the release decision.

## Where Does nLink Store Config/Logs?

Observed in code:

- Installed app config is read from `appsettings.json` in the app base directory or current working directory.
- Operational logs are written to `%LOCALAPPDATA%\nLink\logs\nlink.log`.
- Reliability records are appended to `%LOCALAPPDATA%\nLink\reliability.jsonl`.
- The default NKN identity file is `%LOCALAPPDATA%\nLink\identity.json`.
- Hang reports are written under `%LOCALAPPDATA%\nLink\artifacts\hang\`.

Practical upgrade note:

- `appsettings.json` is packaged with the app in the install directory, so treat it as install-owned content that may be overwritten by the RC installer.
- I did not find a separate user-settings file under `%APPDATA%` or `%LOCALAPPDATA%` outside the paths above in this quick scan.

Code locations checked:

- `src/nLink.Core/Logging/LocalOperationalLog.cs`
- `src/nLink.Core/SessionReliabilityLog.cs`
- `src/nLink.Infra.Nkn/NknTransportOptions.cs`
- `src/nLink.App/Services/HangReportService.cs`
- `installer/nLink.iss`
