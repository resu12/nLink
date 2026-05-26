# NKN Tuna Implementation

This document describes the current nLink NKN Tuna integration. It covers the developer POC, the app sidecar path, wallet linking, session-bound negotiation, runtime unlock behavior, payer selection, spending caps, fallback rules, and the current test expectations.

Tuna remains experimental and default-off. The normal NKN bridge is still the canonical transport for discovery, approval, handshake, chat, remote control, control messages, and fallback.

Related app-payload references:

- [`docs/screenshare-implementation.md`](screenshare-implementation.md) describes the current H.264 screen-share media pipeline.
- [`docs/file-transfer-implementation.md`](file-transfer-implementation.md) describes the current route-aware file-transfer pipeline: regular NKN V4, active Tuna V4, and controlled post-Tuna V6 fallback.

## Goals

- Use Tuna only as an optional acceleration lane for high-volume app payloads.
- Keep nLink's existing consent, session binding, replay protection, sequencing, and application-level encryption authoritative.
- Avoid mandatory token payment in the default consumer flow.
- Make paid Tuna use explicit, bounded, observable, and reversible.
- Keep failure behavior simple: silently fall back to current NKN and keep the approved session alive.

## Non-Goals

- Tuna is not a replacement for the normal NKN bridge.
- Tuna transport encryption is not treated as a replacement for nLink secure envelopes.
- Wallet linking does not automatically enable paid runtime acceleration.
- nLink does not copy wallets or persist wallet passwords.
- Diagnostics and support exports must not expose wallet paths, wallet addresses, seeds, private keys, passwords, or decrypted wallet material.

## Current Architecture

### Canonical NKN Bridge

nLink continues to use the Node bridge with the official JavaScript `nkn-sdk` over JSONL stdin/stdout. This bridge remains responsible for:

- helper and helpee discovery flow,
- explicit help request approval,
- session handshake and verification,
- chat,
- remote control lifecycle and input,
- screen-share control messages,
- file-transfer control messages,
- session end and fallback.

### Go Tuna Tools

Two Go tools exist under `tools/`.

`tools/nkn-tuna-poc/` is the Phase 0 raw-stream benchmark tool. It is standalone and must not be used with real nLink session payloads. See [`docs/tuna-poc-runbook.md`](tuna-poc-runbook.md).

`tools/nkn-tuna-sidecar/` is the app-owned sidecar used by the integrated experiments. It supports:

- `wallet-status`: unlocks a linked wallet once, checks address and balance, emits JSONL, and exits without paying or opening Tuna.
- `listen`: starts the paid listener side with exact peer allow-listing and hard local caps.
- `dial`: starts the free dialing side after a valid session-bound offer.

The sidecar emits status, payment, and summary events as JSONL. Data frames use a local binary IPC protocol, not stdout.

### C# Integration Points

The main runtime pieces are:

- `NknTunaAccelerationOptions`: loads feature flags and sidecar/runtime configuration.
- `NknTunaAccelerationLane`: owns the local acceleration lane, sidecar IPC, dialer/listener lifetime, and fallback state.
- `NknTunaSidecarClient`: speaks the local binary IPC protocol to the sidecar.
- `NknTunaListenerSidecarSupervisor`: starts and monitors the paid listener process.
- `NknSignalingTransport.Acceleration`: handles offer/answer/down negotiation and routes eligible envelopes.
- `ITransportAccelerationControl`: lets the app runtime ask the active transport to stop Tuna without ending the NKN session.
- `TunaRuntimePilotService`: central runtime coordinator for wallet unlock, cooldown, payer intent, listener startup requests, and stop requests.
- `TunaWalletLinkStore`: persists the linked wallet path and public validation metadata.
- `TunaWalletSidecarVerifier`: runs `wallet-status` for one-shot validation.
- `DiagnosticsPageViewModel`: backs the user-facing Options page.
- `SessionHeaderView`: shows the Tuna status pictogram and session-only unlock switch.

## Routing Rules

Only these payloads may use Tuna after a successful session-bound negotiation:

- `MsgType.ScreenShareFrame` on the media lane.
- `MsgType.FileTransferDataFrame` on the bulk/file lane.

These always stay on the current NKN bridge:

- help request, approval, rejection, and session lifecycle,
- session handshake and verification,
- chat,
- remote-control request/start/stop/input/ack/display/state,
- screen-share stop/config/keyframe/recovery/cursor/pressure,
- file-transfer offer/accept/decline/open/cancel/error/complete.

Inbound accelerated frames are injected back into the existing envelope router as serialized nLink envelopes. They still pass the normal secure envelope, source, session, replay, sequencing, capability, and consent checks.

## Session-Bound Negotiation

Tuna negotiation uses the existing secure control path:

- `TransportAccelerationOffer`
- `TransportAccelerationAnswer`
- `TransportAccelerationDown`

nLink must not connect to the local listener sidecar, publish a Tuna address, or start a dialer sidecar until the current session is Tuna-eligible.

A session is Tuna-eligible only when all of these are true:

- invite is validated,
- normal approval is active,
- handshake completed,
- handshake state is verified,
- current session id exists,
- current peer/source address matches,
- at least one file or screen lane is allowed.

Every offer, answer, and down message is validated against the current session:

- exact session id match,
- expected peer/source address match,
- supported sidecar protocol version,
- valid nonce,
- unexpired message,
- at least one mutually supported lane for accepted negotiations.

Hard binding failures never start sidecars and never mark Tuna available. They are logged for diagnostics and otherwise downgrade silently to current NKN.

Hard rejects include:

- wrong session id,
- wrong source/peer,
- bad secure metadata,
- nonce mismatch,
- unsupported version,
- expired message.

Soft local rejects include:

- sidecar unavailable,
- listener unavailable,
- unsupported local lane set,
- no mutually supported lane.

Soft rejects may send a negative answer when useful, then stay on current NKN.

## Wallet Linking

Wallet linking is visible in `Options > Wallet`.

Linking a wallet stores only local state:

- full wallet path,
- linked timestamp,
- last verified timestamp,
- wallet address,
- balance,
- status,
- last failure reason.

The state is stored under `%LOCALAPPDATA%\nLink\tuna-wallet-link.json`.

nLink never copies the wallet file and never persists:

- password,
- seed,
- private key,
- decrypted wallet material.

`Validate balance` opens a hidden password dialog, passes the password to `nlink-tuna-sidecar.exe wallet-status --password-stdin`, reads the public wallet address and balance from JSONL, updates local metadata, clears the password buffer, and exits. This mode must not start a listener, dial Tuna, or spend NKN.

## Runtime Preferences

Runtime settings are separate from the wallet link. They are local preferences, not wallet secrets.

The runtime preference state includes:

- enabled flag,
- selected lanes,
- max price NKN/MB,
- max MiB,
- max duration,
- last runtime status.

The default runtime state is off. A linked and funded wallet does not automatically start Tuna.

The current pilot defaults are:

- max price: `0.0002 NKN/MB`,
- max total: `2048 MiB`,
- max duration: `30 minutes`,
- accept timeout: `120 seconds`,
- lanes: `file` and `screen`.

If both lanes are disabled, the setting is rejected. At least one lane must remain selected.

## Unlock And Switch State Machine

The persistent Options toggle means "this machine may pay after an approved session and a session-only unlock."

The session header switch is session-only:

- off means the wallet is locked for this/next session, or Tuna is unavailable,
- on means the wallet is unlocked for this/next approved session, starting, negotiated, or active.

Both `Options > Wallet > Unlock for this session` and the session header switch use the same runtime coordinator:

- `UnlockForSessionAsync(password, source)`
- `LockOrStopForSessionAsync(reason, source)`
- `GetUnlockStateAsync()`

The UI opens the hidden password dialog and passes the password buffer to the coordinator. The coordinator owns wallet checks, verifier checks, cooldowns, password clearing, runtime status, and stop behavior.

The password is cleared after:

- listener start attempt,
- failed unlock,
- lock/off,
- session end,
- app exit,
- wallet unlink,
- runtime disable,
- validation failure.

Wrong passwords are handled as a recoverable unlock failure:

- the wallet remains linked and verified,
- the password is cleared,
- a friendly error is shown,
- a shared cooldown applies to both Options and the header switch,
- no password is persisted, logged, or exposed through diagnostics.

Unlock attempts are serialized. If the user clicks the header switch or Options unlock button again while a password validation or listener request is already in progress, nLink keeps the existing attempt, clears the extra password buffer, and reports that Tuna unlock is already in progress.

Explicit re-enable is allowed in an approved session after Tuna has fallen back to regular NKN. A fresh successful unlock publishes the friendly state "Trying Tuna again for this session", clears the local user-stopped guard for that new attempt, and sends a new session-bound offer without resetting chat, screen sharing, remote control, or active file transfers. If the peer still has a stale `user_stopped_tuna` rejection from the previous stop, the transport retries quickly while the local listener is already ready. Regular NKN continues to carry eligible frames until the new Tuna negotiation is accepted and healthy.

## Payer Selection

The side that listens pays Tuna providers. The peer dials for free.

Current payer rules:

- If only helpee is unlocked, helpee pays and listens.
- If only helper is unlocked, helper pays and listens after the helpee-priority delay.
- If both sides are unlocked, helpee pays and listens.
- If helper has already started a paid listener and helpee unlocks before Tuna becomes active, helper yields, stops its listener, and becomes the free dialer.
- If the selected payer toggles off before Tuna becomes active, the other unlocked side may try to pay.
- If active Tuna is stopped by user intent, Tuna falls back to current NKN for the rest of that session and does not auto-reselect a new payer.

Local runtime off means "do not pay." The app may still act as the free dialer when the peer is paying.

Payer arbitration is guarded by a per-session payer decision id. Offers, answers, payer-intent messages, and Tuna-down messages carry the current decision id so late messages from a previous attempt cannot flip the current state. A stale answer or stale down message is rejected, logged for diagnostics, and regular NKN remains in use. This is especially important when one side unlocks Tuna while the other side is already starting a listener.

Helper-paid startup uses an adaptive helpee-priority wait. Helper first sends payer intent, then waits briefly for helpee intent. If helpee says it will listen, helper yields. If helpee says it will only dial, helper starts immediately. If no helpee intent arrives within the short grace window, helper proceeds instead of waiting the full priority window. This keeps helper-only Tuna startup shorter while preserving helpee priority when both sides are actually unlocked.

## Stop And Fallback Behavior

Turning the header switch off while waiting clears the in-memory unlock state and no sidecar starts.

Turning it off while Tuna is starting or active:

- stops listener/dialer sidecars,
- closes local IPC,
- marks acceleration unavailable,
- clears outstanding negotiation state,
- emits `TransportAccelerationDown` when session-bound context exists,
- falls back to current NKN,
- keeps chat, screen-share control, file-transfer control, remote control, and the approved session alive.

These situations must not crash the app or end the normal NKN session:

- sidecar missing,
- wrong executable,
- wrong password,
- empty wallet,
- provider timeout,
- listener exit,
- cap reached,
- IPC disconnect,
- wallet unlink,
- runtime disable,
- malformed Tuna negotiation,
- user switch-off while waiting,
- user switch-off while starting,
- user switch-off while active.

File-transfer routing is explicit:

- regular NKN uses `regular_nkn_v4_fast`, protocol `4`,
- active file Tuna uses `file_tuna_v4`, protocol `4`,
- live post-Tuna fallback uses one-shot `post_tuna_fallback_v6`, protocol `6`, for the affected transfer.

When Tuna stops during an active `file_tuna_v4` transfer, nLink live-transitions that same transfer into `post_tuna_fallback_v6` over regular NKN. If Tuna comes back during the same transfer, a later live route epoch can return it to `file_tuna_v4`; another switch-off can transition it back to `post_tuna_fallback_v6`. That V6 route is a one-shot recovery route: after final fallback completion succeeds, the fallback state is consumed and the next new file transfer resolves from current transport state (`file_tuna_v4` when Tuna is active, otherwise `regular_nkn_v4_fast`).

The measured fallback V6 transfer uses transport-epoch proof. New tail traffic on the target transport is blocked until the receiver proves the exact committed frontier can advance there. Generic bridge `Ready` or Tuna `Ready` is not enough to mark the transfer recovered.

The V6 epoch states are:

- `EpochStarting`
- `TargetProofPending`
- `FrontierRepairOnly`
- `BackfillRepair`
- `Recovered`
- `WaitingForTargetTransport`
- `Terminal`

Cancel, pause, resume, session end, peer down, window close, and app exit stay outside this recovery machine. They are hard-priority lifecycle actions over regular NKN control and must terminalize or pause locally without waiting for Tuna, file-data credit, repair queues, or bulk backlog.

## Spending Caps And Accounting

Paid listener starts always include exact peer binding and caps:

- `--allow-remote <exact peer NKN address>`
- `--max-price-nkn-per-mb 0.0002`
- `--max-total-mib 2048`
- `--max-duration-sec 1800`
- `--accept-timeout-sec 120`
- `--local-ipc 127.0.0.1:0`

Caps are user-visible in `Options > Wallet`. From the user's perspective:

- max NKN/MB limits the provider price nLink is willing to use,
- max MiB limits the amount of accelerated app payload for the session,
- max minutes limits the listener runtime,
- if a cap is reached, Tuna stops and current NKN remains available.

Total spend is best-effort because provider accounting can have in-flight usage. nLink keeps local accounting for its own Tuna sessions, not wallet lifetime accounting.

Each app-started paid listener session is also recorded in the local Tuna usage file as a bounded session ledger. A session record includes a run id, start/end time, role, bytes moved, app-payload MB, paid NKN, average NKN/MB when known, payment event count, payment telemetry status, cap reason, fallback reason, and whether the sidecar summary was observed. The ledger keeps the newest 100 session records.

Payment confidence is explicit:

- `reported`: at least one sidecar payment event or cumulative spend summary was observed.
- `no_payment_telemetry_reported`: Tuna moved app payload bytes, but the sidecar reported no payment events.
- `accounting_incomplete`: the listener exited or was stopped before a sidecar summary was observed.
- `none`: no Tuna payload moved in that session.

If Tuna moves data but no payment event is reported, the UI must say `no payment telemetry reported`; it must not imply the traffic was free.

`Options > Wallet` shows:

- Spent by nLink,
- Average cost,
- Last session cost,
- Last session reason.

`Spent by nLink` means locally tracked Tuna spend from nLink sidecar telemetry on this device. It does not mean total NKN spent by the wallet on chain.

## Diagnostics And Status UI

The former Diagnostics page is user-facing `Options` and has three tabs:

- `Settings`: screen-sharing presets and capture environment hints.
- `Wallet`: Tuna wallet link, validation, runtime toggle, lanes, caps, unlock, status, and spend fields.
- `Diagnostics`: support diagnostics, health, logs, counters, bridge state, metrics export, and bug-report actions.

The session header shows a small Tuna pictogram next to the role label:

- gray means inactive or unavailable,
- pulsing means negotiation/start is in progress and the wallet is already unlocked,
- light blue means Tuna is connecting or active on the non-paying dialing side,
- yellow means this computer is selected as the paid Tuna listener side. The tooltip also explains that this computer pays for Tuna traffic while active.

The icon must stay gray and non-pulsing while the wallet is locked. When both sides are unlocked, helpee priority can briefly change the helper from yellow listener-starting state to blue dialer state as the helper yields to the helpee-paid listener.

The session header switch is placed next to the pictogram because it controls the session-only wallet unlock for Tuna.

Useful operational log events include:

- `tuna_runtime_unlocked`
- `tuna_runtime_unlock_failed`
- `tuna_runtime_locked`
- `tuna_acceleration_negotiated`
- `tuna_acceleration_reset`
- `tuna_acceleration_message_rejected`
- `tuna_acceleration_answer_rejected`
- `tuna_listener_sidecar_event`
- `tuna_payment`

Diagnostics copy/export redacts wallet paths, wallet addresses, passwords, seeds, and private-key material.

## Performance Expectations

Phase 3 benchmark work compared current NKN and Tuna using app-level file, screen, and reconnect profiles.

Current user-facing estimate:

> File transfer about 1.8x in Phase 3 benchmark; screen stability improved, latency roughly similar.

This is an estimate, not a guarantee. Tuna remains experimental until installed-app testing confirms:

- file-transfer throughput remains materially better,
- screen-share p95 latency does not materially regress,
- screen drop/stall behavior does not regress,
- reconnect/fallback remains reliable,
- payment telemetry and caps behave predictably.

## Developer Build Notes

Build the app:

```powershell
dotnet build src\nLink.App\nLink.App.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
```

Build the Tuna sidecar:

```powershell
$version = (Get-Content VERSION -Raw).Trim()
go -C tools\nkn-tuna-sidecar build -ldflags "-X main.sidecarVersion=$version" -o ..\..\artifacts\tuna-sidecar\nlink-tuna-sidecar.exe .
```

Build the installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -Runtime win-x64
```

Useful environment overrides for developer tests:

- `NLINK_NKN_TUNA_ENABLED=1`
- `NLINK_NKN_TUNA_SIDECAR_EXE=<path-to-nlink-tuna-sidecar.exe>`
- `NLINK_NKN_TUNA_LANES=file,screen`
- `NLINK_NKN_TUNA_ALLOW_DEGRADED_PROVIDER_READY=1`
- `NLINK_NKN_TUNA_REQUIRE_STRICT_PROVIDER_READY=1`
- `NLINK_NKN_TUNA_DEGRADED_PROVIDER_GRACE_SECONDS=20`

The advanced Options runtime pilot can also enable Tuna locally without changing default startup behavior. The paid listener now requires full provider readiness before advertising Tuna as usable for the session. Recent GUI/NKN runs showed that degraded 3-path startup can connect and then fail almost immediately with `remote_closed` / `terminal_tuna_write_failed`, so degraded provider readiness is now diagnostic-only. `NLINK_NKN_TUNA_ALLOW_DEGRADED_PROVIDER_READY=1` re-enables degraded readiness for explicit A/B experiments, while `NLINK_NKN_TUNA_REQUIRE_STRICT_PROVIDER_READY=1` keeps strict readiness even if that diagnostic override is present. `NLINK_NKN_TUNA_DEGRADED_PROVIDER_GRACE_SECONDS` is diagnostic-only and defaults to `0`; when set with degraded readiness enabled, nLink waits that many seconds for full 4-path readiness after 3-path degraded readiness appears.

Run focused paid Tuna route checks from a developer machine:

```powershell
dotnet build tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
$env:NLINK_RUN_MANUAL_BRIDGE = "1"
$env:NLINK_RUN_TUNA_PHASE6_SHORT_MATRIX = "1"
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
dotnet test tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~TunaSidecarPhase6_ShortPaidMatrix"
dotnet build-server shutdown
```

The historical Phase 6 short matrix writes artifacts under `artifacts/tuna-sidecar/phase6-short-<timestamp>/`. Read `phase6-operator-verdict.txt` first when reviewing those artifacts. New pre-installer release evidence should use `tools\Run-FileTransferRouteAcceptance.ps1`, which validates regular NKN V4, active Tuna V4, and controlled post-Tuna V6 fallback. Repeated paid cells should use the build-once plus `--no-build --no-restore` pattern from `docs/build-test-lock-avoidance.md` to avoid Windows generated-output locks. For provider-path troubleshooting, compare `provider-quality-report.json` across strict readiness (`NLINK_TUNA_TEST_REQUIRE_PROVIDER_READY=1`, `NLINK_TUNA_TEST_PROVIDER_READY_ATTEMPTS=3`) and explicit degraded diagnostic runs (`NLINK_TUNA_TEST_DEGRADED_PROVIDER_GRACE_SECONDS=20`).

Historical local Phase 6 file-transfer reference run:

- Artifact root: `artifacts/tuna-sidecar/phase6-short-20260512T151146Z/`
- Verdict: `PASS`
- Cells: `12/12`
- Caveats: this historical run allowed degraded 3-path provider startup and all cells reported `provider_paths_degraded`; use it as historical fallback evidence only. Current runtime policy requires full provider readiness before Tuna is advertised as usable, and current active file Tuna uses `file_tuna_v4`.

The wider opt-in Tuna soak matrix remains available when a longer paid pass is warranted:

```powershell
$env:NLINK_RUN_MANUAL_BRIDGE = "1"
$env:NLINK_RUN_TUNA_SOAK_MATRIX = "1"
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
$env:NLINK_TUNA_SOAK_TIERS = "core,extended"
$env:NLINK_TUNA_SOAK_DURATION_MIN = "15"
dotnet test tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj --filter "FullyQualifiedName~TunaSidecar_SoakMatrix_FileScreenAcrossPayersPresetsFaults"
```

The wider soak matrix writes artifacts under `artifacts/tuna-sidecar/soak-matrix-<timestamp>/`. It covers Tuna helpee-paid, Tuna helper-paid, both-unlocked payer selection, app-restart setup, sidecar crash, switch-off fallback, provider-timeout cells, and both High quality and Tuna quality screen presets. `NLINK_TUNA_SOAK_FILE_PACING_MBPS` defaults to `8` so mixed file-plus-screen soak traffic is sustained without intentionally saturating the Tuna sidecar queues. During a Tuna-down fallback, the soak file stream slows to an NKN-safe fallback pace and gets a longer drain window so the test proves fallback progress instead of flooding the fallback path. If Tuna drops unexpectedly near the tail of a long no-fault or restart-before-traffic cell, the cell may pass with an `unexpected_tuna_drop_recovered` warning only when NKN fallback proof is complete for file and screen and at least 98% of the file stream was received. Soak readiness uses live app-side Tuna counters first, then sidecar `bridge_frame_forwarded` log evidence as a fallback when the lane has already reset and live counters are unavailable. Use `NLINK_TUNA_SOAK_CELL_FILTER=core-tuna-file-helpee-switch-off` or a comma-separated list of cell ids to rerun one failing cell before spending on the full matrix.

The app-side Tuna IPC queue defaults to 1024 frames. Bulk/file queue timeout is still treated as a hard acceleration failure because mixing file chunk order across Tuna and NKN is risky. Media/screen queue timeout is treated as per-frame backpressure: that frame falls back to NKN, Tuna remains available, and logs record `tuna_sidecar_queue_backpressure`.

## Test Coverage Checklist

Automated coverage should include:

- default app startup never starts Tuna,
- linked/funded wallet alone does not enable Tuna,
- runtime toggle without a verified funded wallet stays on current NKN,
- listener never starts before approved verified session eligibility,
- listener starts with exact peer allow-list, caps, linked wallet path, and password stdin only,
- wrong password clears the password and starts cooldown,
- Options unlock and header switch share the same coordinator behavior,
- switch off while waiting clears unlock state,
- switch off while starting or active stops Tuna and falls back to NKN,
- both-lanes-disabled setting is rejected,
- only file data and screen media frames can route through Tuna,
- chat, consent, handshake, remote control, screen control, and file control never route through Tuna,
- wrong session/source/nonce/version/expiry messages cannot enable Tuna or tear down the session,
- payment telemetry updates local nLink Tuna spend,
- diagnostics redaction removes wallet paths, wallet addresses, passwords, seeds, and private keys.

Manual installed-app coverage should include:

- link and validate a low-balance wallet,
- unlock from Options,
- unlock from the session header switch,
- toggle off before session approval,
- toggle off during Tuna startup,
- toggle off during active screen sharing,
- toggle off during active file transfer,
- only helper unlocked,
- only helpee unlocked,
- both sides unlocked with helpee paying,
- sidecar missing,
- wrong password,
- cap reached,
- app exit and session end cleanup.

## Go/No-Go Before Runtime Promotion

Do not promote Tuna beyond experimental unless:

- normal consumer flow remains unchanged when Tuna is off,
- no paid listener starts before consent and verified session context,
- every paid listener launch has exact peer binding and hard spending caps,
- live installed two-app tests accelerate screen and file traffic reliably,
- payment telemetry records spend and average cost accurately enough for the UI,
- fallback is automatic and does not end the approved nLink session,
- diagnostics/export contains no secrets or unredacted wallet metadata,
- Windows packaging includes the sidecar and verifier path consistently.
