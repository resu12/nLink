# NKN Tuna Implementation

This document describes the current nLink NKN Tuna integration. It covers the developer POC, the app sidecar path, wallet linking, session-bound negotiation, runtime unlock behavior, payer selection, spending caps, fallback rules, and the current test expectations.

Tuna remains experimental and default-off. The normal NKN bridge is still the canonical transport for discovery, approval, handshake, chat, remote control, control messages, and fallback.

Related app-payload references:

- [`docs/screenshare-implementation.md`](screenshare-implementation.md) describes the current H.264 screen-share media pipeline.
- [`docs/file-transfer-implementation.md`](file-transfer-implementation.md) describes the current V4 file-transfer data-session pipeline.

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

## Payer Selection

The side that listens pays Tuna providers. The peer dials for free.

Current payer rules:

- If only helpee is unlocked, helpee pays and listens.
- If only helper is unlocked, helper pays and listens after the helpee-priority delay.
- If both sides are unlocked, helpee pays and listens.
- If the selected payer toggles off before Tuna becomes active, the other unlocked side may try to pay.
- If active Tuna is stopped by user intent, Tuna falls back to current NKN for the rest of that session and does not auto-reselect a new payer.

Local runtime off means "do not pay." The app may still act as the free dialer when the peer is paying.

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

`Options > Wallet` shows:

- Spent by nLink,
- Average cost,
- Last session cost,
- Expected improvement.

`Spent by nLink` means locally tracked Tuna spend from nLink sidecar telemetry on this device. It does not mean total NKN spent by the wallet on chain.

## Diagnostics And Status UI

The former Diagnostics page is user-facing `Options` and has three tabs:

- `Settings`: screen-sharing presets and capture environment hints.
- `Wallet`: Tuna wallet link, validation, runtime toggle, lanes, caps, unlock, status, and spend fields.
- `Diagnostics`: support diagnostics, health, logs, counters, bridge state, metrics export, and bug-report actions.

The session header shows a small Tuna pictogram next to the role label:

- gray means inactive or unavailable,
- pulsing means negotiation/start is in progress,
- light blue means Tuna is active.

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
dotnet build src\nLink.App\nLink.App.csproj -c Release
```

Build the Tuna sidecar:

```powershell
go -C tools\nkn-tuna-sidecar build -o ..\..\artifacts\tuna-sidecar\nlink-tuna-sidecar.exe .
```

Build the installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -Runtime win-x64
```

Useful environment overrides for developer tests:

- `NLINK_NKN_TUNA_ENABLED=1`
- `NLINK_NKN_TUNA_SIDECAR_EXE=<path-to-nlink-tuna-sidecar.exe>`
- `NLINK_NKN_TUNA_LANES=file,screen`

The advanced Options runtime pilot can also enable Tuna locally without changing default startup behavior.

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
