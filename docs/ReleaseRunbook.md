# Release Runbook

This runbook describes the version-neutral steps to ship `v<version>` from a clean Windows release shell.

It is the canonical release process; use [rc-validation-checklist.md](release/rc-validation-checklist.md) for sign-off. A successful build, source checkpoint, or operator-analyzer PASS does not by itself qualify a release. Do not tag or publish before mandatory security/route proofs, payload provenance, packaging checks, and CI sign-off are established.

## Current Qualification Update (2026-10-15)

`0.7.0` remains unpublished. The selected runtime and portable are from `cf8ea2963e2533d1fa32069fe6f4a9fa2f78c13c`; later documentation edits do not replace or rebuild those bytes.

- **Seven-scenario strict GUI correctness: PASS.** `artifacts/rc-cf8ea296-seven-gui-20261015/campaign-result.json` indexes the original per-scenario summaries/manifests. All seven ran once with escalated strict preflights and no automatic retries. Actual delivery, SHA integrity, required coordinator publications, fallback, and terminal GUIs passed, including helper-to-helpee payer handoff, both Tuna activations in the 256 MiB route cycle, and the subsequent 16 MiB Tuna transfer.
- **Authenticated delivery/security closeout: PASS.** `security-closeout.json` under that campaign root binds all 10 required Tuna delivery stages to 20 exact authenticated send/receive witnesses, valid security-context lifetimes, coordinator publications, writer evidence, and full/compact parity. This supplements the preserved reports; it does not rewrite their original verdicts or re-evaluate route authority.
- **Performance remains separate:** two scenario PASS, two WARN, three FAIL. Transport/repair contamination, fallback throughput warnings, and optional diagnostic publication/readiness warnings remain retained. No correctness rule, numeric baseline, or limit was changed; no throughput guarantee is made.
- **Exact-payload packaging: PASS.** Selected assets are only `artifacts/release-candidate-20261015/cf8ea296/assets/0.7.0/`: installer, unchanged portable ZIP, and `SHA256SUMS.txt`. `package-provenance.json` binds the 14-file stage and original qualification. No portable rebuild occurred during installer creation. Installer SHA-256: `82e88ba946758fcc37c79a9944fc577c9138186bc7d6f7d3b950764f580c9b21`; portable SHA-256: `b64bcc0565dbcd9e298471693db2c1a266d88f1e25a53743db8c942d10661d69`. Do not substitute older default release assets.
- **Installer qualification: PASS.** The candidate's `qualification/guest-evidence/installer-validation.json` records an isolated upgrade (historical baseline: nLink 0.6.2 -> candidate `0.7.0`), two self-tests, all 14 installed hashes, locked bundled-Node cleanup, uninstall, and synthetic identity/reliability/log/hang-data preservation. `qualification/wizard-evidence/visual-review.json` records the 0.7.0 destination page; no interactive installation occurred. Both guests had networking/clipboard disabled. Host installation and real user settings were not migration-test inputs.
- **Regression coverage is retained by scope, not blanket inheritance.** The October 13 `8667444a` gate passed 5,931 executions; unchanged covered paths retain that evidence. `artifacts/release-gate-018bfd0d-20261015/` and `artifacts/remaining-bounded-gates-20261015/` cover the later affected runtime/analyzer/harness paths. The original harness file-lock failure remains recorded; `artifacts/bound-responder-lifetime-20261015/` contains the subsequently passing PowerShell 5.1/7 fixture checks. No new all-Core batch sequence was started.
- **Bounded runtime readiness: PASS.** The unchanged packaged runtime outside the restricted sandbox passed 500/500 DevLocal reliability cycles, the resource gate, and 200/200 leak cycles; all final active counters returned to zero. Evidence: `qualification/readiness-unsandboxed/result.json`. The original `qualification/readiness/result.json` remains unchanged: restricted-sandbox .NET bundle extraction exited `-2147450721` before application execution. These are local readiness checks, not a new live-network run or an aggregate BetaReadiness/CI verdict.
- **Bounded promotion checks: PASS.** BridgeStabilityPromotion passed 8/8. After three documentation-only historical-baseline clarifications under the existing guard, the affected documentation check passed 1/1 and ContractFreeze passed 57/57, with no skips. Evidence: `qualification/documentation-correction/result.json`; the original 56/57 failure and logs remain unchanged under `qualification/promotion-gates/`. Runtime code, tests, acceptance rules and packaged bytes were not changed. The Contracts build succeeded with 23 warnings, including unavailable NuGet advisory data; warnings are retained. All 14 staged payload hashes and both release asset hashes still match. This is bounded local promotion evidence, not an aggregate BetaReadiness or CI verdict.
- **Remaining release sign-off:** review/checkpoint the documentation and obtain green CI for the final reviewed checkpoint. CI has `workflow_dispatch`; pushing `v0.7.0` alone does not trigger it. No push, tag, or publication is authorized by this local closeout. Unsigned Windows assets remain the previously accepted 0.7.0 exception, not a valid-signature claim. Existing optional diagnostic deferrals remain in [test-lanes.md](test-lanes.md).

## Historical Targeted Qualification (2026-10-12)

The targeted 256 MiB route-cycle blocker is closed on runtime source `964fe2134c00236380392a97feea238991cb27d5`. This is targeted evidence, not full qualification of a new release payload:

- Preserved live run: `artifacts/r964fe213-route-cycle-validation-20261012/gui/20261012T041635Z/regular-v4-live-activation-off-on-off-256mb/`. Its original campaign remains recorded as `FAIL` / `incomplete_proof`; do not overwrite that history.
- Analyzer-only reanalysis: `artifacts/r964fe213-sanitized-identity-reanalysis-20261012/retained-analysis/`. Strict analysis exited `0`, with runtime correctness `pass` and overall `WARN_EXTERNAL_TRANSPORT`. All five admitted stall-grace episodes recovered; none remains unresolved. The authenticated Tuna route-cycle contract and full/compact snapshot parity pass. The two transport-churn warnings remain recorded.
- The unchanged 97,436-event input has SHA-256 `6a1e262b7af8bf5358081cd2c3cae92094a683f89e4ab88e07ac809c8ab3136b`. Evidence includes completed GUI delivery, matching payload SHA-256, and coordinator-published `regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6`, with authenticated delivery at both Tuna activations. No new live run was performed for the analyzer fix.
- Focused regression evidence: 27 passing admission/handoff checks in `artifacts/r964fe213-analyzer-admission-reanalysis-20261012/focused-tests/focused-regressions-confirmation.trx`, followed by 14 passing publication-identity/admission/handoff checks in `artifacts/r964fe213-sanitized-identity-reanalysis-20261012/focused-tests/sanitized-identity-regressions.trx`. These are overlapping focused selections, not a full-suite result. Runtime code, acceptance rules, and limits were unchanged by these analyzer fixes.
- Next release qualification: one planned broad regression gate on a stable checkpoint, then a fresh portable rebuild and strict GUI campaign, explicitly covering helper-to-helpee payer handoff. Live preflights require escalation and must pass before scenarios start. Preserve failures and stop for classification rather than automatically expanding patches. Final packaging must use the qualified payload; final CI, installer qualification, and release approval remain separate obligations.

## 0.7.0 Candidate Evidence (2026-10-08)

Historical package evidence follows; see the current qualification update above for later runtime/analyzer work.

`0.7.0` remains an unpublished candidate. Retained evidence applies to the covered source, contracts, and exact payload, not automatically to later builds:

- **GUI correctness PASS / performance FAIL:** all six strict Phase 5 scenarios passed on runtime source `bd177028c47cee6bb992b6f505dad7ff803d1532` on October 7. Evidence root: `artifacts/final-gui-matrix-bd177028-20261007/20261007T175211Z/`; read `phase5-analyzer-gui-acceptance-summary.txt/.json` and `route-acceptance-run-manifest.json`. This proves actual delivery, SHA integrity, coordinator-published routes, authenticated Tuna delivery (including both cycle activations), fallback, and terminal GUIs for those runs. It is not a throughput guarantee or coverage of every topology.
- **Qualified package:** `artifacts/release-candidate-20261008/830a695c/assets/0.7.0/` contains installer, portable ZIP, and `SHA256SUMS.txt`. `package-provenance.json` in the candidate root binds packaging checkpoint `830a695c` to the exact GUI-qualified 14-file payload. Runtime/route-contract source was unchanged from the qualified checkpoint. Do not substitute the older default `artifacts/releases/0.7.0/` contents.
- **Separate rebuild is unqualified:** the candidate root's `portable/nLink/win-x64/` rebuild has different executable/sidecar hashes and is not exact-binary GUI-qualified. Its `installer/` output is not the selected release asset. Preserve it as build evidence, not a replacement pass.
- **New payer-handoff fix is pending live qualification:** `artifacts/payer-handoff-fix-20261008/` contains the listener-start collision regression, 96 passing focused checks, and a verified portable rebuild/self-test. App SHA-256 is `5b8a3e2f24c737f6d677d1256b0af215ba69c50acb6e24494e179f917312147b`. It is not the qualified installer payload and does not inherit the October 7 matrix PASS. The next targeted check is helper-paid Tuna -> explicit off/fallback -> helpee-paid reactivation, with authenticated Tuna delivery, coordinator-published protocol-4 routes, SHA integrity, and terminal GUI completion.
- **Installer lifecycle PASS:** isolated Windows Sandbox validated upgrade (historical baseline: nLink 0.6.2 -> candidate `0.7.0`), two app self-tests, all 14 installed hashes, locked bundled-Node handling, uninstall, and preservation of synthetic identity/reliability/log/hang fixtures. Evidence: `qualification/guest-evidence/installer-validation.json` under the candidate root. This did not modify the host installation and does not prove all real user settings migrations.
- **Wizard inspection:** the 0.7.0 destination page opened in Windows Sandbox; no interactive install was performed. The separate exit record is launch/cleanup evidence, not a second installation pass.
- **Remaining sign-off:** CI for the final reviewed checkpoint is not established. The current CI workflow has `workflow_dispatch`; a push to `v0.7.0` alone does not trigger it. Unsigned Windows artifacts are the already accepted 0.7.0 exception, not an Authenticode pass. Approved optional diagnostic-test deferrals remain explicitly recorded in [test-lanes.md](test-lanes.md).

These local artifacts are not public downloads. Preserve their original failures, warnings, manifests, and checksums; publishing needs a separate release decision.

## Preflight

For a planned broad release regression gate, run from the repo root. During implementation, select only focused affected tests and applicable retained coverage; do not automatically continue through Core batches or repeatedly run aggregate analyzer/contract lanes.

```powershell
dotnet build .\nLink.sln -c Release -m:1 -nr:false -p:UseSharedCompilation=false
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane CoreContract,FileTransferContract,FileTransferAnalyzer,FileTransferAcceptanceFake,Smoke -Configuration Release
powershell -ExecutionPolicy Bypass -File .\tools\BetaReadiness-Check.ps1
dotnet build-server shutdown
```

Optional GUI smoke requires an interactive Windows desktop. Live NKN/GUI scenarios additionally need escalated execution outside a restricted sandbox and successful live preflights:

The Phase 5 matrix includes `helper-to-helpee-payer-handoff-128mb`: establish Helper-paid Tuna, switch Helper off, enable Helpee-paid Tuna during the same transfer, then switch Helpee off and finish on fallback. Its required published route sequence is `file_tuna_v4 -> post_tuna_fallback_v6 -> file_tuna_v4 -> post_tuna_fallback_v6`; payload must be observed in both Tuna phases, with SHA integrity and both terminal GUIs. The October 15 campaign provides current exact-payload evidence; it is not automatically inherited by later builds. For a future targeted fix, after focused checks and a fresh portable rebuild, select only it with `Run-FileTransferRouteAcceptance.ps1 -MatrixMode phase5-analyzer-gui-acceptance -ScenarioName helper-to-helpee-payer-handoff-128mb -StrictCampaignPreflight`, supplying the fresh executable/sidecar paths and existing test-wallet inputs. Do not bypass the clean-checkpoint, isolation, or live-network preflights.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane GuiSmoke -Configuration Release
```

Expected outcome:

- build passes
- deterministic contract, analyzer, fake-acceptance, and compatibility smoke lanes pass
- GUI smoke passes when run
- BetaReadiness reports `PASS`
- reliability and packaging gates pass

Test ownership lanes are documented in `docs\test-lanes.md`. Prefer named lanes for local validation instead of invoking retired project paths.
Windows generated-output lock avoidance is documented in `docs\build-test-lock-avoidance.md`; use that pattern for repeated manual or paid opt-in cells.

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
- `artifacts\bridge\<runtime>\bridge-manifest.json` (`appVersion`, generated by `installer\Build-BridgeBundle.ps1`; do not hand-edit for release)
- `docs\releases\<version>.md`
- `docs\releases\<version>-github.md`

Quick check:

```powershell
Get-Content .\VERSION
Get-Content .\installer\nLink.iss | Select-String "AppVersion|OutputBaseFilename"
powershell -ExecutionPolicy Bypass -File .\installer\Build-BridgeBundle.ps1 -Runtime win-x64
(Get-Content .\artifacts\bridge\win-x64\bridge-manifest.json -Raw | ConvertFrom-Json).appVersion
```

Expected outcome:
- the bridge manifest `appVersion` exactly matches `VERSION`
- stale bridge bundles fail `Build-Portable.ps1`, `Build-Installer.ps1`, and `build\verify-package-manifest.ps1`; rebuild the bridge bundle instead of carrying an old manifest forward

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
- current code must describe file transfer as route-aware, single-file, explicit accept/decline, and protected by nLink session envelope/source validation rather than by assuming NKN alone is sufficient
- current production file-transfer routes are `regular_nkn_v4_fast` protocol `4`, `file_tuna_v4` protocol `4`, and one-shot live `post_tuna_fallback_v6` protocol `6`; a successful final post-Tuna fallback transfer must consume that fallback state so the next new transfer resolves from current transport state (`file_tuna_v4` when Tuna is active, otherwise regular V4); `diagnostic_regular_nkn_v6` is unsafe opt-in only

Transport abuse-resistance limit matrix:
- `NknSignalingTransport` high-priority control queue: `256` items max
- `NknSignalingTransport` low-priority control queue: `256` items max, stale mouse-move entries coalesce to latest
- `NknSignalingTransport` file-transfer data-session queue: `512` frames and `32 MiB` estimated queued bytes per active data session
- file-transfer overflow policy: log `filetransfer_data_session_overflow`, fail closed with `ReceiverBufferExhausted`, remove the active data-session registration, and require resume/reopen
- file-transfer data-session path: sender/source validation is bound to the negotiated remote bulk endpoint and selected route
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
- run the route acceptance gate before installer creation: cover the full seven-scenario `phase5-analyzer-gui-acceptance` mode with `-StrictCampaignPreflight` after rebuilding the portable bridge/runtime bundle; run live-network preflights and GUI validation outside the restricted sandbox with explicit escalation, and never bypass a failed preflight. For stop-on-first-failure campaigns, invoke and inspect each scenario separately rather than relying on the matrix wrapper's continued result collection
- verify regular NKN completion/integrity on route `regular_nkn_v4_fast` protocol `4`, no `filetransfer_data_session_overflow`, no `filetransfer_message_rejected`, no bridge stdout protocol violations, no regular-NKN bridge bulk send/clear failures, and no unexpected diagnostic V6 route
- verify active Tuna completion/integrity on route `file_tuna_v4` protocol `4`, including actual authenticated Tuna payload delivery, coordinator publication, SHA integrity, and terminal GUI completion; active `file_tuna_v6` is invalid
- verify live fallback completion/integrity on final route `post_tuna_fallback_v6` protocol `6` with explicit live-route epoch proof; fallback speed is separated as performance variance; verify the following new transfer resolves from current transport state when the fallback transfer completed successfully
- the 256 MiB route-cycle scenario must prove coordinator-published `regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6`, including authenticated Tuna payload delivery at both activations; SHA OK or fallback completion alone cannot substitute for the second Tuna publication and delivery
- helper-to-helpee payer handoff must prove `file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6`, including authenticated payload delivery with each payer and final fallback completion
- Phase 5 release correctness remains strict for coordinator authority, authentication, route/protocol invariants, payload delivery, integrity, and terminal GUI completion; performance is a separate verdict, not a throughput guarantee. Retain performance FAIL, warnings, and contaminated measurements without relabeling them as passes or changing numeric baselines and limits
- `post_completion_late_sender_frame` ignored frames are allowed only when they occur after terminal completion for a recently completed transfer; retain the count from the soak summary with the release evidence
- retain `filetransfer-live-nkn-summary.txt`, `filetransfer-live-nkn-cycles.jsonl`, and the retained log slice with the release evidence, together with the Phase 5 summary, run manifest, full scenario artifacts, and both peers' retained slices

## Packaging

Build the portable payload before GUI qualification:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Portable.ps1 -Runtime win-x64
```

The portable builder publishes the app, builds the pinned bridge and Go Tuna sidecar, validates the package, and creates the ZIP by default. It needs .NET 8 and Go; installer compilation also needs Inno Setup 6. Helper/helpee alias folders are optional. A standalone `Build-Installer.ps1 -Runtime win-x64` calls the portable builder again before compiling, rather than merely packaging the prior stage.

Planned one-shot validation and packaging path:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\PreRelease-Check.ps1 -RunGuiSmoke -RunBetaReadiness -RunFileTransferRouteAcceptanceGate
```

Use this one-shot path for a planned final regression gate, not repeated diagnostic batches. During bounded qualification, retain applicable evidence for unchanged code and contracts and run only missing or affected checks through their named tools; mandatory security and route proofs still require sign-off.

Supply a low-balance test wallet via `-FileTransferRouteAcceptanceWalletPath` when not using the default fixture path, and provide the session-only password to that scoped process through `NLINK_TUNA_TEST_WALLET_PASSWORD`; do not put a password literal in a saved command or log. Clear it after the run. Omitting `-RunFileTransferRouteAcceptanceGate` explicitly skips route qualification.

Exact-payload caution: the one-shot script rebuilds during installer creation after the GUI gate. Compare final app, bridge, sidecar, and package hashes with the qualified payload before signing off; source equality alone does not make different bytes exact-binary qualified. If hashes differ, keep the rebuild unqualified and either validate that final payload or package the proven payload with reviewed provenance. Do not silently transfer a PASS to it.

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
Get-AuthenticodeSignature .\artifacts\releases\<version>\nLink-Setup-win-x64-<version>.exe | Format-List Status,StatusMessage,SignerCertificate
Get-AuthenticodeSignature .\artifacts\portable\nLink\win-x64\nLink.exe | Format-List Status,StatusMessage,SignerCertificate
```

Bridge supply-chain checks:
- `installer\Build-BridgeBundle.ps1` must use the pinned Node runtime path unless `-UseSystemNode` is intentionally used for local experiments
- the Node archive SHA-256 must match the pinned value before extraction
- `tools\nkn-bridge\package-lock.json` is required; there is no `npm install` fallback
- bridge dependencies restore with clean `npm ci --ignore-scripts --no-audit --no-fund`
- packaged bridge output must include `package-lock.json`, `bridge-manifest.json`, and `bridge-dependencies.json`
- packaged bridge output must have no shipped `node_modules`
- `bridge-manifest.json` must record `nodeModulesShipped=false`, the package-lock hash, Node archive hash, npm version, ncc version, and bridge package version
- `bridge-manifest.json` must record `appVersion=<version>` from `VERSION`; packaging must fail if this field is stale after a version bump
- optional online evidence may include `npm audit --omit=dev`, but advisory lookup is not mandatory for offline/local release builds

Signing policy:
- preferred public release artifacts are Authenticode-signed before publish
- at minimum, signed releases verify:
  - `artifacts\releases\<version>\nLink-Setup-win-x64-<version>.exe`
  - `artifacts\portable\nLink\win-x64\nLink.exe`
- local/manual packaging runs may remain unsigned until the signing step
- for `0.7.0`, unsigned public Windows artifacts are an accepted release exception; record that exception in release evidence and do not claim Authenticode signing for those artifacts

Expected outcome:
- package manifest checks pass
- release staging contains no `.pdb`, `.xml`, `Avalonia.Diagnostics.dll`, or `nLink.runtimeconfig.dev.json`
- packaged app contains and uses the bundled `bridge\win-x64\node.exe` and `bridge\win-x64\index.js` without relying on `NLINK_NKN_NODE_PATH` or `NLINK_NKN_BRIDGE_PATH`
- packaged app uses the documented file-transfer queue bounds and bridge binary caps
- Authenticode status is `Valid` for the public installer and installed app binary, or the unsigned-artifact exception is explicitly recorded for this release
- installer remains per-user and non-admin (`{localappdata}\Programs\nLink`, `PrivilegesRequired=lowest`)
- no release packaging step depends on `NLINK_INVITE_MODE=legacy_signed`

## Git Tag

After mandatory sign-off and explicit publication approval, create and push the release tag. Do not use the tag itself to establish CI: check the current workflow triggers first.

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
- Options opens from Home
- install does not request admin elevation
- uninstall leaves no running processes from the install directory

Safe invite flow sanity check:

- Normal helper-bound request flow is active by default:
  - helper waiting screen shows a copyable helper address
  - helpee enters that address and clicks `Request help`
  - helper accepts the incoming request
  - both sides compare the same five-symbol handshake-derived verification sequence before the helpee clicks `Allow`
  - QR/share/copy technical paths must not bypass binding, approval, or verification
- Options -> Diagnostics -> Copy diagnostics includes:
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
  - README and release notes may describe chat, remote control, screen share, file transfer, and lifecycle traffic as nLink-managed post-approval application-layer protected traffic
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
