# Release Runbook

This runbook describes the exact steps to ship `0.4.2`.

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
- performance gate passes

## Version Bump Locations

Primary version source:

```powershell
Get-Content .\VERSION
```

Current expected value:

```text
0.4.2
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
- `artifacts\releases\0.4.2\nLink-Portable-win-x64-0.4.2.zip`
- `artifacts\releases\0.4.2\nLink-Setup-win-x64-0.4.2.exe`
- `artifacts\releases\0.4.2\SHA256SUMS.txt`

Verify artifacts:

```powershell
Get-ChildItem .\artifacts\releases\0.4.2
Get-Content .\artifacts\releases\0.4.2\SHA256SUMS.txt
```

Packaging robustness checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\verify-package-manifest.ps1 -StageDir .\artifacts\portable\nLink\win-x64 -ManifestPath .\installer\package-manifest.win-x64.txt
powershell -ExecutionPolicy Bypass -File .\build\verify-package-manifest.ps1 -StageDir .\artifacts\portable\helper\win-x64 -ManifestPath .\installer\package-manifest.win-x64.txt
Get-AuthenticodeSignature .\artifacts\releases\0.4.2\nLink-Setup-win-x64-0.4.2.exe | Format-List Status,StatusMessage,SignerCertificate
Get-AuthenticodeSignature .\artifacts\portable\helper\win-x64\nLink.exe | Format-List Status,StatusMessage,SignerCertificate
```

Expected outcome for `0.4.2`:
- package manifest checks pass
- release staging contains no `.pdb`, `.xml`, `Avalonia.Diagnostics.dll`, or `nLink.runtimeconfig.dev.json`
- Authenticode status is currently expected to be unsigned unless signing infrastructure is added in a later release
- installer remains per-user and non-admin (`{localappdata}\Programs\nLink Helper`, `PrivilegesRequired=lowest`)

## Git Tag

Create and push the release tag:

```powershell
git tag v0.4.2
git push origin v0.4.2
```

## GitHub Release

Create a GitHub release with:
- Tag: `v0.4.2`
- Title: `nLink 0.4.2`

Attach:
- `artifacts\releases\0.4.2\nLink-Setup-win-x64-0.4.2.exe`
- `artifacts\releases\0.4.2\nLink-Portable-win-x64-0.4.2.zip`
- `artifacts\releases\0.4.2\SHA256SUMS.txt`

Paste release notes from:
- `docs\releases\0.4.2.md`

Link current beta issues guidance from:
- `docs\KnownIssues.md`

## Post-Release

Run a quick sanity install test:

```powershell
Start-Process .\artifacts\installer\nLink-Setup-win-x64-0.4.2.exe
```

Verify:
- installer launches
- app starts
- installed app `--self-test` exits `0`
- Home screen appears
- Helper flow opens
- Helpee flow opens
- session pages show the shared header and shell layout
- Diagnostics opens from Home
- install does not request admin elevation
- uninstall leaves no running processes from the install directory

Portable sanity check:

```powershell
Expand-Archive .\artifacts\releases\0.4.2\nLink-Portable-win-x64-0.4.2.zip -DestinationPath .\artifacts\portable-smoke -Force
Start-Process .\artifacts\portable-smoke\nLink.exe
```

Upgrade sanity check:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\validate-upgrade-uninstall.ps1 `
  -OldInstallerPath .\artifacts\releases\0.4.0\nLink-Setup-win-x64-0.4.0.exe `
  -NewInstallerPath .\artifacts\releases\0.4.2\nLink-Setup-win-x64-0.4.2.exe
```

## Rollback Notes

- If the GitHub release draft is wrong, delete the draft release and re-upload corrected assets.
- If the tag was pushed incorrectly:

```powershell
git tag -d v0.4.2
git push origin :refs/tags/v0.4.2
```

- If an installed build needs cleanup, use the generated uninstaller from the install directory or rerun the previous known-good installer.
