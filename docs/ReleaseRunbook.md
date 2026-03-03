# Release Runbook

This runbook describes the exact steps to ship `0.2.0-beta.5`.

## Preflight

Run from the repo root on Windows:

```powershell
dotnet build .\nLink.sln -c Release
dotnet test .\tests\nLink.SmokeTests\nLink.SmokeTests.csproj -c Release --filter Category=Smoke
$env:NLINK_RUN_GUI_SMOKE='1'
dotnet test .\tests\nLink.SmokeTests\nLink.SmokeTests.csproj -c Release --filter Category=GuiSmoke
Remove-Item Env:NLINK_RUN_GUI_SMOKE -ErrorAction SilentlyContinue
powershell -ExecutionPolicy Bypass -File .\tools\BetaReadiness-Check.ps1
```

Expected outcome:
- build passes
- smoke tests pass
- GUI smoke passes
- BetaReadiness reports `PASS`

## Version Bump Locations

Primary version source:

```powershell
Get-Content .\VERSION
```

Current expected value:

```text
0.2.0-beta.5
```

Version-related files to verify:
- `VERSION`
- `installer\nLink.iss` (`AppVersion` fallback used by direct Inno compilation)

Quick check:

```powershell
Get-Content .\VERSION
Get-Content .\installer\nLink.iss | Select-String "AppVersion|OutputBaseFilename"
```

## Packaging

Build the bundled bridge, portable ZIP, and installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-BridgeBundle.ps1 -Runtime win-x64
powershell -ExecutionPolicy Bypass -File .\installer\Build-Portable.ps1 -Runtime win-x64
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -Runtime win-x64
```

Preferred one-shot validation + packaging path:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\PreRelease-Check.ps1 -RunGuiSmoke -RunBetaReadiness
```

Expected release outputs:
- `artifacts\releases\0.2.0-beta.5\nLink-Portable-win-x64-0.2.0-beta.5.zip`
- `artifacts\releases\0.2.0-beta.5\nLink-Setup-win-x64-0.2.0-beta.5.exe`
- `artifacts\releases\0.2.0-beta.5\SHA256SUMS.txt`

Verify artifacts:

```powershell
Get-ChildItem .\artifacts\releases\0.2.0-beta.5
Get-Content .\artifacts\releases\0.2.0-beta.5\SHA256SUMS.txt
```

## Git Tag

Create and push the release tag:

```powershell
git tag v0.2.0-beta.5
git push origin v0.2.0-beta.5
```

## GitHub Release

Create a GitHub pre-release with:
- Tag: `v0.2.0-beta.5`
- Title: `nLink 0.2.0-beta.5`
- Mark as pre-release

Attach:
- `artifacts\releases\0.2.0-beta.5\nLink-Setup-win-x64-0.2.0-beta.5.exe`
- `artifacts\releases\0.2.0-beta.5\nLink-Portable-win-x64-0.2.0-beta.5.zip`
- `artifacts\releases\0.2.0-beta.5\SHA256SUMS.txt`

Paste release notes from:
- `docs\releases\0.2.0-beta.5.md`

Link current beta issues guidance from:
- `docs\KnownIssues.md`

## Post-Release

Run a quick sanity install test:

```powershell
Start-Process .\artifacts\installer\nLink-Setup-win-x64-0.2.0-beta.5.exe
```

Verify:
- installer launches
- app starts
- Home screen appears
- Helper flow opens
- Helpee flow opens
- session pages show the shared header and shell layout
- Diagnostics opens from Home

Portable sanity check:

```powershell
Expand-Archive .\artifacts\releases\0.2.0-beta.5\nLink-Portable-win-x64-0.2.0-beta.5.zip -DestinationPath .\artifacts\portable-smoke -Force
Start-Process .\artifacts\portable-smoke\nLink.exe
```

## Rollback Notes

- If the GitHub pre-release is wrong, delete the draft/pre-release and re-upload corrected assets.
- If the tag was pushed incorrectly:

```powershell
git tag -d v0.2.0-beta.5
git push origin :refs/tags/v0.2.0-beta.5
```

- If an installed build needs cleanup, use the generated uninstaller from the install directory or rerun the previous known-good installer.
