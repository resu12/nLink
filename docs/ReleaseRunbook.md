# Release Runbook

This runbook describes the exact steps to ship `0.4.5`.

## Preflight

Run from the repo root on Windows:

```powershell
dotnet build .\nLink.sln -c Release
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane Smoke -Configuration Release
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane GuiSmoke -Configuration Release
powershell -ExecutionPolicy Bypass -File .\tools\BetaReadiness-Check.ps1
```

Expected outcome:
- build passes
- smoke tests pass
- GUI smoke passes
- BetaReadiness reports `PASS`
- reliability and packaging gates pass

Test ownership lanes are documented in `docs\test-lanes.md`. Prefer named lanes for local validation instead of invoking a retired monolith project path.

Invite-security preflight:

```powershell
Get-ChildItem Env:NLINK_INVITE_MODE,Env:NLINK_ALLOW_INSECURE_LEGACY_INVITE_MODE,Env:NLINK_ALLOW_INSECURE_LEGACY_INVITE_SIGNING,Env:NLINK_ALLOW_INSECURE_UNBOUND_PUBLIC_INVITES -ErrorAction SilentlyContinue
```

Expected outcome:
- no invite-security override env vars are set in the release shell
- if `NLINK_INVITE_MODE` is set at all, release validation stops and the shell is cleaned before continuing

Transport/app-layer security contract:
- release notes and README must distinguish transport security from nLink application-layer security
- current code may claim nLink application-layer protection for chat, remote control, screen share, and session lifecycle traffic after approval
- current code must still distinguish those nLink guarantees from the remaining trust placed in the bundled NKN bridge/runtime

Transport abuse-resistance limit matrix:
- `NknSignalingTransport` high-priority control queue: `256` items max
- `NknSignalingTransport` low-priority control queue: `256` items max, stale mouse-move entries coalesce to latest
- `NknSignalingTransport` screen-share outbound gate wait budget: `25 ms`
- `NknSignalingTransport` replay windows: bounded per control, lifecycle, and screen-share family
- `NknSignalingTransport` high-lane overflow policy:
  - `ControlStop` may displace queued non-stop work
  - `ControlDisplayInfo` and `ControlStateSnapshot` may coalesce when full
  - other non-stop high-lane control messages are rejected at capacity
- bridge/session payload ceilings remain enforced below this file:
  - bridge input/output framing limits
  - secure-envelope validation limits
  - screen-share payload/chunk budgets
- release validation must review both transport-local queue limits and lower-layer payload limits together

## Version Bump Locations

Primary version source:

```powershell
Get-Content .\VERSION
```

Current expected value:

```text
0.4.5
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
- `artifacts\releases\0.4.5\nLink-Portable-win-x64-0.4.5.zip`
- `artifacts\releases\0.4.5\nLink-Setup-win-x64-0.4.5.exe`
- `artifacts\releases\0.4.5\SHA256SUMS.txt`

Verify artifacts:

```powershell
Get-ChildItem .\artifacts\releases\0.4.5
Get-Content .\artifacts\releases\0.4.5\SHA256SUMS.txt
```

Packaging robustness checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\verify-package-manifest.ps1 -StageDir .\artifacts\portable\nLink\win-x64 -ManifestPath .\installer\package-manifest.win-x64.txt
powershell -ExecutionPolicy Bypass -File .\build\verify-package-manifest.ps1 -StageDir .\artifacts\portable\helper\win-x64 -ManifestPath .\installer\package-manifest.win-x64.txt
Get-AuthenticodeSignature .\artifacts\releases\0.4.5\nLink-Setup-win-x64-0.4.5.exe | Format-List Status,StatusMessage,SignerCertificate
Get-AuthenticodeSignature .\artifacts\portable\helper\win-x64\nLink.exe | Format-List Status,StatusMessage,SignerCertificate
```

Signing policy:
- public release artifacts must be Authenticode-signed before publish
- at minimum:
  - `artifacts\releases\0.4.5\nLink-Setup-win-x64-0.4.5.exe`
  - `artifacts\portable\helper\win-x64\nLink.exe`
- local/manual packaging runs may remain unsigned until the signing step, but an unsigned artifact must not be published as the public release build

Expected outcome for `0.4.5`:
- package manifest checks pass
- release staging contains no `.pdb`, `.xml`, `Avalonia.Diagnostics.dll`, or `nLink.runtimeconfig.dev.json`
- Authenticode status is `Valid` for the public installer and installed app binary
- installer remains per-user and non-admin (`{localappdata}\Programs\nLink Helper`, `PrivilegesRequired=lowest`)
- no release packaging step depends on `NLINK_INVITE_MODE=legacy_signed`

## Git Tag

Create and push the release tag:

```powershell
git tag v0.4.5
git push origin v0.4.5
```

## GitHub Release

Create a GitHub release with:
- Tag: `v0.4.5`
- Title: `nLink 0.4.5`

Attach:
- `artifacts\releases\0.4.5\nLink-Setup-win-x64-0.4.5.exe`
- `artifacts\releases\0.4.5\nLink-Portable-win-x64-0.4.5.zip`
- `artifacts\releases\0.4.5\SHA256SUMS.txt`

Paste release notes from:
- `docs\releases\0.4.5.md`

Link current beta issues guidance from:
- `docs\KnownIssues.md`

## Post-Release

Run a quick sanity install test:

```powershell
Start-Process .\artifacts\installer\nLink-Setup-win-x64-0.4.5.exe
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

Safe invite flow sanity check:
- Helper-bound invite flow is active by default:
  - helper waiting screen shows a copyable helper address before any invite is shared
  - helper waiting screen also shows a short verification code derived from that address
  - helpee waiting screen does not show share/copy actions until a valid helper address is entered
  - after entering a valid helper address, share/copy/refresh invite-code actions appear
  - helpee waiting screen shows the bound helper address and its verification code
  - helpee waiting screen shows an invite code by default; raw invite token is only available in technical details
- Diagnostics -> Copy diagnostics includes:
  - `invite_security_mode: issued_one_time_secret_invites`
  - `invite_signing_configuration: not_used_in_issued_secret_mode`
  - `invite_public_flow: verified_helper_required`
  - `invite_security_release_ready: Yes`
  - `invite_security_warning: none`
  - `security_relevant_overrides:`
  - `high_priority_control_queue_overflows:`
  - `high_priority_control_rejected:`
  - `high_priority_control_coalesced:`
  - `high_priority_control_dropped_for_stop:`
- Security wording sanity check:
  - README and release notes may describe chat, remote control, screen share, and lifecycle traffic as nLink-managed post-approval application-layer protected traffic
  - transport-level encryption claims are kept distinct from app-layer security claims
  - docs still mention the remaining trust boundary around the bundled NKN bridge/runtime and reported source identities
- No release validation step or smoke shell sets:
  - `NLINK_ALLOW_INSECURE_LEGACY_INVITE_MODE`
  - `NLINK_ALLOW_INSECURE_LEGACY_INVITE_SIGNING`
  - `NLINK_ALLOW_INSECURE_UNBOUND_PUBLIC_INVITES`

Portable sanity check:

```powershell
Expand-Archive .\artifacts\releases\0.4.5\nLink-Portable-win-x64-0.4.5.zip -DestinationPath .\artifacts\portable-smoke -Force
Start-Process .\artifacts\portable-smoke\nLink.exe
```

Upgrade sanity check:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\validate-upgrade-uninstall.ps1 `
  -OldInstallerPath .\artifacts\releases\0.4.0\nLink-Setup-win-x64-0.4.0.exe `
  -NewInstallerPath .\artifacts\releases\0.4.5\nLink-Setup-win-x64-0.4.5.exe
```

## Rollback Notes

- If the GitHub release draft is wrong, delete the draft release and re-upload corrected assets.
- If the tag was pushed incorrectly:

```powershell
git tag -d v0.4.5
git push origin :refs/tags/v0.4.5
```

- If an installed build needs cleanup, use the generated uninstaller from the install directory or rerun the previous known-good installer.
