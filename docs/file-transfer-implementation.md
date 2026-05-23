# File Transfer Implementation

This document describes the current nLink file-transfer implementation. It is an engineering reference for the runtime pipeline. For operator flow, retained-log analysis, and soak commands, start with [`docs/file-transfer-operability.md`](file-transfer-operability.md).

## Current Status

- Scope: single-file transfer only.
- Not supported yet: folders, drag-and-drop, and resume after app restart.
- Consent boundary: receiving a file requires explicit accept or decline.
- Safety cap: file transfers are capped at `25 GiB` by the app release policy.
- Destination: received files are saved to the Windows Downloads folder by default, with a numbered suffix when the target name already exists.
- Protocols: production uses protocol `4` for regular NKN and active file Tuna, and protocol `6` only for controlled post-Tuna fallback. Protocol `5` is obsolete and rejected.
- Transport boundary: normal NKN remains the default. Experimental Tuna can carry only `MsgType.FileTransferDataFrame` on the bulk lane after session-bound Tuna negotiation succeeds.

File-transfer control stays on current NKN. Offer, accept, decline, session-open, cancel, error, and complete control messages are not accelerated by Tuna.

## Production Routes

Route selection is centralized in `FileTransferRouteResolver`. Sender offer, receiver accept, session-open, runtime start, telemetry, and bridge recovery policy are expected to consume the same `FileTransferRouteSelection`.

| Route | Token | Protocol | Runtime profile | Frame family | Bridge policy |
| --- | --- | --- | --- | --- | --- |
| Regular NKN | `regular_nkn_v4_fast` | `4` | `regular_nkn_v4_fast` | `v4` | `regular_nkn_v4_fast` |
| Active file Tuna | `file_tuna_v4` | `4` | `file_tuna_v4_fast` | `v4` | `tuna_strict` |
| Post-Tuna fallback | `post_tuna_fallback_v6` | `6` | `default_v6` | `v6` | `post_tuna_fallback_strict` |
| Diagnostic regular NKN V6 | `diagnostic_regular_nkn_v6` | `6` | `primary_regular_nkn_bulk_v6` | `v6` | `primary_regular_nkn_quiet` |

Selection precedence:

1. Post-Tuna file fallback active -> `post_tuna_fallback_v6`.
2. Active file Tuna -> `file_tuna_v4`.
3. Explicit unsafe diagnostic regular-NKN V6 opt-in -> `diagnostic_regular_nkn_v6`.
4. Otherwise -> `regular_nkn_v4_fast`.

Tuna configured, eligible, unlocked, funded, inactive, or failed without fallback is not enough to select V6. Screen-share-only acceleration is also not a file-transfer V6 selector.

`file_tuna_v6` and all V5 frame families are obsolete. They must not be emitted by release defaults and are treated as unsupported input.

## High-Level Flow

1. The helper and helpee complete the normal nLink invite, approval, and verified session handshake.
2. The session grants the file-transfer capability.
3. A sender chooses one file.
4. `SessionFileTransferService` resolves one route and includes its route token in `FileTransferOfferV2`.
5. The receiver accepts or declines. Accepted transfers carry the selected route in `FileTransferAcceptV1`.
6. On acceptance, both sides exchange `FileTransferSessionOpenV2` with the route token.
7. The transport opens an `IFileTransferDataSession` for the session and transfer id.
8. The selected route dispatches to the matching runtime:
   - `regular_nkn_v4_fast` and `file_tuna_v4` use the V4 sender/receiver.
   - `post_tuna_fallback_v6` and diagnostic V6 use the V6 sender/receiver.
9. The receiver writes chunks, commits progress, repairs missing ranges, and verifies the final SHA-256.
10. The transfer ends with complete, cancel, or error state on both sides.

## Main Components

### Core Contracts And Models

- `src/nLink.Core/FileTransfer/FileTransferRoute.cs`
  Defines route status inputs, route tokens, route metadata, and the pure resolver.
- `src/nLink.Core/FileTransfer/IFileTransferSignalingTransport.cs`
  Defines file-transfer control sends, data-session opening, and data-session receive/send.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.cs`
  Owns transfer state, offer/accept flow, route application, progress, hard-priority pause/resume/cancel, telemetry, and completion.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV4.cs`
  Implements the production V4 sender/receiver used by regular NKN and active file Tuna.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV6.cs`
  Implements the V6 sender/receiver used by post-Tuna fallback and diagnostic regular-NKN V6. It includes post-Tuna fallback survival diagnostics and frontier repair behavior.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.TransportEpochV6.cs`
  Implements V6 transport-epoch recovery used by the V6 fallback/diagnostic paths.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.Liveness.cs`
  Implements heartbeat/liveness and peer-disconnect terminalization.
- `src/nLink.Core/FileTransfer/SessionFileTransferModels.cs`
  Defines descriptors, snapshots, flow policies, terminal states, and progress models.
- `src/nLink.Core/FileTransfer/FileTransferProtocol.cs`
  Defines current protocol constants and message type names. V5 constants are intentionally removed.
- `src/nLink.Core/FileTransfer/FileTransferDataFrameV4.cs`
  Defines V4 data-frame records plus current V6 records.
- `src/nLink.Core/FileTransfer/FileTransferDataFrameCodec.cs`
  Encodes and decodes compact binary V4 and V6 data frames. V5 frame codes are rejected.
- `src/nLink.Core/FileTransfer/FileTransferPayloadCodec.cs`
  Encodes and decodes file-transfer control payloads, including route-token normalization and protocol/route mismatch rejection.
- `src/nLink.Core/FileTransfer/FileTransferChunkBudget.cs`
  Defines safe chunk and batch sizing limits.
- `src/nLink.Core/FileTransfer/FileTransferPayloadEfficiencyProfile.cs`
  Selects chunk/batch profiles through `NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_PROFILE`.

### App Path

- `src/nLink.App/Services/SessionRuntime.cs`
  Wires file-transfer requests, incoming offers, accept/decline, safe destination planning, and visible session state.
- `src/nLink.App/ViewModels/FileTransferPanelItemViewModel.cs`
  Presents progress, pause/resume/cancel state, and terminal status.
- `src/nLink.App/Views/ChatView.axaml`
  Hosts the visible file-transfer panel inside the chat/session UI.
- `src/nLink.App/Views/HelperPageView.axaml.cs` and `src/nLink.App/Views/HelpeePageView.axaml.cs`
  Handle file picker selection from either role.

### NKN Transport Path

- `src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs`
  Implements control payload routing, data-session behavior, secure envelope validation, data-frame dispatch, transport-origin metadata, lifecycle control payloads, route status, epoch proof messages, and file-transfer diagnostics.
- `src/nLink.Infra.Nkn/NknEnvelopeRouter.cs`
  Routes `MsgType.FileTransferDataFrame` to file-transfer handling.

## Protocol Shape

### Control Messages

Control messages use the secure nLink session control path:

- `offer.v2`
- `accept.v1`
- `decline.v1`
- `session_open.v2`
- `cancel.v1`
- `error.v1`
- `complete.v1`
- pause/resume control
- V6 heartbeat and recovery proof messages when the selected route is V6

`offer.v2`, `accept.v1`, and `session_open.v2` may carry `fileTransferRoute`. Missing route tokens remain compatible when local context can infer the route; invalid or protocol-mismatched route tokens are rejected as transport-incompatible.

### Data Frames

V4 routes use V4 frames:

- `manifest.v4`
- `state.v4`
- `chunk_batch.v4`
- `complete.v4`
- `cancel.v4`
- `error.v4`
- `pause_control.v4`

V6 fallback/diagnostic routes use V6 frames:

- `manifest.v6`
- `receiver_state.v6`
- `chunk_batch.v6`
- `transport_epoch.v6`
- `transport_probe.v6`
- `frontier_request.v6`
- `repair_proof.v6`
- `complete.v6`
- `cancel.v6`
- `error.v6`
- `pause_control.v6`
- `heartbeat.v6`

Only the serialized data frame envelope is eligible for Tuna, and only when it is sent as `MsgType.FileTransferDataFrame` on `NknBridgeChannel.Bulk`.

## Sizing And Batching

The current implementation keeps payloads bounded before they reach the bridge:

- Maximum raw chunk: `48 KiB`.
- Maximum raw batch: `64 KiB`.
- Maximum serialized chunk payload: `50 KiB`.
- Maximum serialized batch payload: `64 KiB`.
- Default V4/Tuna chunk size: `21 KiB`.
- Default maximum V4/Tuna batch segments: `3`.
- V6 fallback keeps the same raw bridge payload ceilings and may use V6-specific receiver-state/frontier repair windows.

Payload efficiency profiles are available for focused tuning:

- `current`
- `packed_3x20kib`
- `packed_3x21kib`
- `large_single_48kib`

The environment variable is `NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_PROFILE`. Mixed screen-share coexistence can be controlled with `NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_ALLOW_SCREENSHARE`.

## Flow Control And Recovery

Regular NKN and active file Tuna use the V4 runtime. V4 progress is receiver-confirmed and may repair missing ranges while preserving terminal correctness and SHA verification. If Tuna is disabled during an active `file_tuna_v4` transfer, that same transfer remains V4 and recovers over regular NKN; it is not canceled and restarted as V6.

Post-Tuna fallback uses a fresh one-shot V6 measured transfer. It is receiver-driven: the receiver reports what it has durably accepted and requests missing ranges; the sender pumps only within advertised budget or explicit frontier repair. Current production evidence shows this V6 fallback path is slower and more variable than the V4 regular/Tuna path, so V6 is not a throughput optimization. It is retained only as a one-shot recovery route after Tuna fallback, plus explicit unsafe diagnostics. After a successful `post_tuna_fallback_v6` transfer, the fallback route is consumed and the next new file transfer returns to regular V4 unless Tuna is active again.

Recovery behaviors include:

- missing-range repair,
- sender repair cache,
- sparse receiver writes where the destination stream supports seek/read/write,
- mixed screen-share pressure handling,
- sender pump depth and pending-byte limits,
- sender-side enforcement that chunks overlap an active receiver request,
- pause/resume control from either side,
- V6 fallback frontier rescue when a post-Tuna fallback transfer reaches sustained frontier pressure.

The transfer treats integrity and terminal correctness as more important than raw throughput.

## Controlled Post-Tuna Fallback

Active file Tuna is V4. When Tuna stops during an active `file_tuna_v4` transfer, nLink does not mutate that live session into V6. The live transfer proves regular-NKN fallback in place and can complete naturally, cancel, or fail with normal terminal semantics.

The controlled fallback model is restart-based:

1. A setup transfer proves `file_tuna_v4`.
2. Tuna is stopped or forced unavailable.
3. Setup cleanup reaches terminal/cleanup evidence.
4. A fresh measured transfer resolves one-shot `post_tuna_fallback_v6`.
5. The measured transfer must complete with protocol `6`, route consistency, SHA/integrity OK, and completed terminals.
6. If that measured transfer completes successfully, the post-fallback V6 route is consumed and the next new transfer resolves to regular V4.

Fallback speed is informational. Survival, route correctness, integrity, and clean terminal completion are the gate.

Do not promote `post_tuna_fallback_v6` to a sticky or performance route. V4 remains the faster production path for regular NKN and active Tuna; V6 fallback is accepted for recovery survival even when its goodput is below the V4/Tuna baselines.

The V6 fallback survival path logs:

- `filetransfer_v6_post_tuna_fallback_survival_policy_enabled`
- `filetransfer_v6_post_tuna_fallback_frontier_rescue_requested`
- `filetransfer_v6_post_tuna_fallback_sender_frontier_rescue_queued`
- `filetransfer_v6_post_tuna_fallback_send_timeout_requeued`

Bridge queue clears remain hard failures for regular NKN. Recovered bridge cleanup evidence during successful post-Tuna fallback may be downgraded to warnings only when route consistency, SHA, and terminals pass.

## Security And Consent

File transfer is protected by the same application-level security model as the rest of nLink:

- The session must be approved and verified.
- File-transfer capability must be granted.
- The receiver must explicitly accept the offer.
- Control and data payloads are bound to the active session.
- Expected peer/source address checks remain active.
- Transfer id and session id checks remain active.
- Replay and sequencing checks remain active.
- Final SHA-256 verification must pass before a received file is considered complete.

Tuna transport encryption, when present, is not a replacement for nLink's secure envelope. Tuna is only an optional delivery lane for already protected file data frames.

## Pause, Cancel, And Completion

Both sides can pause, resume, or cancel active transfers. A transfer can end as:

- completed,
- declined,
- canceled,
- failed.

Restarting either app does not resume a partial transfer. Partial files must not be presented as resumable release artifacts.

Lifecycle actions are hard-priority and local-first. Cancel, pause, resume, session end, peer down, window close, app exit, and transport detach bypass data queues, repair queues, Tuna, bulk backlog, and V6 fallback recovery state.

When a lifecycle action is requested:

- the local transfer card updates immediately,
- transfer lifetime work is stopped or paused locally,
- a best-effort lifecycle notice is sent over regular NKN control,
- duplicate lifecycle notices are idempotent,
- late data frames after terminalization are dropped with rate-limited diagnostics.

Complete wins only when checksum/final terminal state was committed first. Otherwise hard lifecycle terminal state wins.

## Diagnostics

The main support surfaces are:

- Options -> Diagnostics -> Copy diagnostics.
- `tools\FileTransfer-Ops.ps1`.
- Retained logs under `%LOCALAPPDATA%\nLink\logs`.
- File-transfer analyzer artifacts.

Useful diagnostics include transfer ids, route token, protocol version, terminal state, bridge bulk health, file-transfer message rejections, V4/V6 frame evidence, route consistency, V6 fallback frontier rescue, receiver buffer pressure, repair/reorder summaries, coexistence summaries, lifecycle-priority markers, and external transport health summaries.

The first retained-analysis file to read is `filetransfer-operator-verdict.txt`.

Route-aware logs must include `filetransfer_route_selected`. Route/protocol/runtime/bridge-policy mismatches are hard failures. Legacy no-route logs can still be classified as legacy compatibility evidence, but V5 and `file_tuna_v6` evidence are obsolete-protocol failures.

## Current Limits

- Single-file transfers only.
- No folder transfer yet.
- No drag-and-drop yet.
- No resume after app restart.
- Default file-size cap is `25 GiB`.
- Live throughput depends on NKN, Tuna provider health, and network delivery.
- Tuna remains experimental and optional. Current NKN remains the default path when Tuna is inactive.

## Validation

For deterministic local regression checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast
```

Before installer creation, the route acceptance gate must pass:

- regular NKN 64 MiB quick and 128 MiB target: `regular_nkn_v4_fast`, protocol `4`, SHA OK, completed terminals, no regular bridge bulk failures; goodput is recorded for release notes and regression triage but is not a hard pre-installer gate on public NKN.
- active Tuna 128 MiB no-fault: `file_tuna_v4`, protocol `4`, SHA OK, completed terminals, goodput greater than `4,000,000 B/s`.
- controlled fallback 128 MiB: setup `file_tuna_v4` may cancel cleanly; measured transfer must be `post_tuna_fallback_v6`, protocol `6`, SHA OK, completed terminals. Fallback speed is informational.

For operator flow and artifact interpretation, use [`docs/file-transfer-operability.md`](file-transfer-operability.md).
