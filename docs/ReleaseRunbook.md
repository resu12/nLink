# Release Runbook

This runbook describes the version-neutral steps to ship `v<version>` from a clean Windows release shell.

## Preflight

Run from the repo root:

```powershell
dotnet build .\nLink.sln -c Release
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane Smoke -Configuration Release
powershell -ExecutionPolicy Bypass -File .\tools\BetaReadiness-Check.ps1
```

Optional GUI smoke requires an interactive Windows desktop:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane GuiSmoke -Configuration Release
```

Expected outcome:
- build passes
- smoke tests pass
- GUI smoke passes when run
- BetaReadiness reports `PASS`
- reliability and packaging gates pass

Test ownership lanes are documented in `docs\test-lanes.md`. Prefer named lanes for local validation instead of invoking retired project paths.

Support evidence and bug-report expectations are documented in `docs\supportability.md`.

Release-shell unsafe override preflight:

```powershell
Get-ChildItem Env:NLINK_UNSAFE_DEVELOPER_MODE,Env:NLINK_TRANSPORT,Env:NLINK_NKN_*,Env:NLINK_FILETRANSFER_*,Env:NLINK_SCREENSHARE_UNSAFE_*,Env:NLINK_BRIDGE_REUSE_MODE,Env:NLINK_BRIDGE_KEEPALIVE_IDLE_TIMEOUT_SECONDS,Env:NLINK_DOWNLOAD_URL,Env:NLINK_REPO_URL -ErrorAction SilentlyContinue
```

Expected outcome:
- the public release shell does not have `NLINK_UNSAFE_DEVELOPER_MODE` set
- no transport, bridge/runtime path, NKN topology/recovery, file-transfer tuning/test, unsafe media, or release-link override env vars are present
- repo tools that intentionally need unsafe test overrides, such as GUI smoke and live file-transfer soak, set `NLINK_UNSAFE_DEVELOPER_MODE=1` only for their child test/app processes and restore the previous environment afterward

## Version Bump Locations

Primary version source:

```powershell
Get-Content .\VERSION
```

Version-related files to verify:
- `VERSION`
- `installer\nLink.iss` (`AppVersion` fallback used by direct Inno compilation)
- `docs\releases\<version>.md`
- `docs\releases\<version>-github.md`

Quick check:

```powershell
Get-Content .\VERSION
Get-Content .\installer\nLink.iss | Select-String "AppVersion|OutputBaseFilename"
```

## Security And Transport Preflight

Invite-security preflight:

```powershell
Get-ChildItem Env:NLINK_INVITE_MODE,Env:NLINK_ALLOW_INSECURE_LEGACY_INVITE_MODE,Env:NLINK_ALLOW_INSECURE_LEGACY_INVITE_SIGNING,Env:NLINK_ALLOW_INSECURE_UNBOUND_PUBLIC_INVITES -ErrorAction SilentlyContinue
```

Expected outcome:
- no invite-security override env vars are set in the release shell
- if `NLINK_INVITE_MODE` is set at all, release validation stops and the shell is cleaned before continuing

Transport/app-layer security contract:
- release notes and README must distinguish transport security from nLink application-layer security
- current code may claim nLink application-layer protection for chat, remote control, screen share, file transfer, and session lifecycle traffic after approval
- current code must still distinguish those nLink guarantees from the remaining trust placed in the bundled NKN bridge/runtime
- current code must describe file transfer as V4-only, single-file, explicit accept/decline, and protected by nLink session envelope/source validation rather than by assuming NKN alone is sufficient

Transport abuse-resistance limit matrix:
- `NknSignalingTransport` high-priority control queue: `256` items max
- `NknSignalingTransport` low-priority control queue: `256` items max, stale mouse-move entries coalesce to latest
- `NknSignalingTransport` file-transfer data-session queue: `512` frames and `32 MiB` estimated queued bytes per active data session
- file-transfer overflow policy: log `filetransfer_data_session_overflow`, fail closed with `ReceiverBufferExhausted`, remove the active data-session registration, and require resume/reopen
- file-transfer V4 bulk path: sender/source validation is bound to the negotiated remote bulk endpoint
- `NknSignalingTransport` screen-share outbound gate wait budget: `25 ms`
- `NknSignalingTransport` replay windows: bounded per control, lifecycle, and screen-share family
- `NknSignalingTransport` high-lane overflow policy:
  - `ControlStop` may displace queued non-stop work
  - `ControlDisplayInfo` and `ControlStateSnapshot` may coalesce when full
  - other non-stop high-lane control messages are rejected at capacity
- bridge/session payload ceilings remain enforced below this file:
  - bridge binary framing: `64 KiB` payload cap, `65,535` primary text bytes, `65,535` secondary text bytes, `196,606` body bytes before allocation
  - secure-envelope validation limits
  - screen-share payload/chunk budgets
- release validation must review both transport-local queue limits and lower-layer payload limits together

File-transfer release gate:
- run at least one live NKN file-transfer soak on the packaged app after building the bridge/runtime bundle
- verify completion/integrity, no `filetransfer_data_session_overflow`, no `filetransfer_message_rejected`, no bridge stdout protocol violations, and no unexpected downgrade from the V4 data path
- `post_completion_late_sender_frame` ignored frames are allowed only when they occur after terminal completion for a recently completed transfer; retain the count from the soak summary with the release evidence
- retain `filetransfer-live-nkn-summary.txt`, `filetransfer-live-nkn-cycles.jsonl`, and the retained log slice with the release evidence

## Packaging

Build the bundled bridge, portable ZIP, and installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-BridgeBundle.ps1 -Runtime win-x64
powershell -ExecutionPolicy Bypass -File .\installer\Build-Portable.ps1 -Runtime win-x64
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -Runtime win-x64
```

Preferred one-shot validation and packaging path:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\PreRelease-Check.ps1 -RunGuiSmoke -RunBetaReadiness
```

Expected release outputs:
- `artifacts\releases\<version>\nLink-Portable-win-x64-<version>.zip`
- `artifacts\releases\<version>\nLink-Setup-win-x64-<version>.exe`
- `artifacts\releases\<version>\SHA256SUMS.txt`

Verify artifacts:

```powershell
Get-ChildItem .\artifacts\releases\<version>
Get-Content .\artifacts\releases\<version>\SHA256SUMS.txt
```

Packaging robustness checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\verify-package-manifest.ps1 -StageDir .\artifacts\portable\nLink\win-x64 -ManifestPath .\installer\package-manifest.win-x64.txt
powershell -ExecutionPolicy Bypass -File .\build\verify-package-manifest.ps1 -StageDir .\artifacts\portable\helper\win-x64 -ManifestPath .\installer\package-manifest.win-x64.txt
Get-AuthenticodeSignature .\artifacts\releases\<version>\nLink-Setup-win-x64-<version>.exe | Format-List Status,StatusMessage,SignerCertificate
Get-AuthenticodeSignature .\artifacts\portable\helper\win-x64\nLink.exe | Format-List Status,StatusMessage,SignerCertificate
```

Signing policy:
- public release artifacts must be Authenticode-signed before publish
- at minimum:
  - `artifacts\releases\<version>\nLink-Setup-win-x64-<version>.exe`
  - `artifacts\portable\helper\win-x64\nLink.exe`
- local/manual packaging runs may remain unsigned until the signing step, but an unsigned artifact must not be published as the public release build

Expected outcome:
- package manifest checks pass
- release staging contains no `.pdb`, `.xml`, `Avalonia.Diagnostics.dll`, or `nLink.runtimeconfig.dev.json`
- packaged app contains and uses the bundled `bridge\win-x64\node.exe` and `bridge\win-x64\index.js` without relying on `NLINK_NKN_NODE_PATH` or `NLINK_NKN_BRIDGE_PATH`
- packaged app uses the documented file-transfer queue bounds and bridge binary caps
- Authenticode status is `Valid` for the public installer and installed app binary
- installer remains per-user and non-admin (`{localappdata}\Programs\nLink Helper`, `PrivilegesRequired=lowest`)
- no release packaging step depends on `NLINK_INVITE_MODE=legacy_signed`

## Git Tag

Create and push the release tag:

```powershell
git tag v<version>
git push origin v<version>
```

## GitHub Release

Create a GitHub release with:
- Tag: `v<version>`
- Title: `nLink <version>`

Attach:
- `artifacts\releases\<version>\nLink-Setup-win-x64-<version>.exe`
- `artifacts\releases\<version>\nLink-Portable-win-x64-<version>.zip`
- `artifacts\releases\<version>\SHA256SUMS.txt`

Paste release notes from:
- `docs\releases\<version>.md`

Link current support guidance from:
- `docs\KnownIssues.md`
- `docs\supportability.md`

## Post-Release

Run a quick sanity install test:

```powershell
Start-Process .\artifacts\installer\nLink-Setup-win-x64-<version>.exe
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
  - helper waiting screen does not show a separate address-derived verification code
  - helpee waiting screen does not show share/copy actions until a valid helper address is entered
  - after entering a valid helper address, share/copy/refresh invite-code actions appear
  - helpee waiting screen shows the bound helper address and helper identity tag
  - helpee waiting screen shows an invite code by default; raw invite token is only available in technical details
  - approval screens on both sides show the same five-symbol handshake-derived verification sequence before allow/accept completes
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
- Outside scoped child-process harnesses, the operator's release shell has none of:
  - `NLINK_UNSAFE_DEVELOPER_MODE`
  - `NLINK_ALLOW_INSECURE_LEGACY_INVITE_MODE`
  - `NLINK_ALLOW_INSECURE_LEGACY_INVITE_SIGNING`
  - `NLINK_ALLOW_INSECURE_UNBOUND_PUBLIC_INVITES`
  - `NLINK_NKN_NODE_PATH`
  - `NLINK_NKN_BRIDGE_PATH`
  - any `NLINK_FILETRANSFER_*` tuning/test override outside the scoped soak harness child process

Portable sanity check:

```powershell
Expand-Archive .\artifacts\releases\<version>\nLink-Portable-win-x64-<version>.zip -DestinationPath .\artifacts\portable-smoke -Force
Start-Process .\artifacts\portable-smoke\nLink.exe
```

Upgrade sanity check:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\validate-upgrade-uninstall.ps1 `
  -OldInstallerPath <previous-signed-installer-path> `
  -NewInstallerPath .\artifacts\releases\<version>\nLink-Setup-win-x64-<version>.exe
```

## Rollback Notes

- If the GitHub release draft is wrong, delete the draft release and re-upload corrected assets.
- If the tag was pushed incorrectly:

```powershell
git tag -d v<version>
git push origin :refs/tags/v<version>
```

- If an installed build needs cleanup, use the generated uninstaller from the install directory or rerun the previous known-good installer.
