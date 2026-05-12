# File Transfer Implementation

This document describes the current nLink file-transfer implementation. It is an engineering reference for the runtime pipeline. For operator flow, retained-log analysis, and soak commands, start with [`docs/file-transfer-operability.md`](file-transfer-operability.md).

## Current Status

- Protocol: V6-only in the current release. V5, V4, null, or unsupported peers are rejected cleanly as transport-incompatible.
- Scope: single-file transfer only.
- Not supported yet: folders, drag-and-drop, and resume after app restart.
- Consent boundary: receiving a file requires explicit accept or decline.
- Safety cap: file transfers are capped at `25 GiB` by the app release policy.
- Destination: received files are saved to the Windows Downloads folder by default, with a numbered suffix when the target name already exists.
- Transport boundary: normal NKN remains the default. Experimental Tuna can carry only `MsgType.FileTransferDataFrame` on the bulk lane after session-bound Tuna negotiation succeeds.

File-transfer control stays on current NKN. Offer, accept, decline, session-open, cancel, error, and complete control messages are not accelerated by Tuna.

## High-Level Flow

1. The helper and helpee complete the normal nLink invite, approval, and verified session handshake.
2. The session grants the file-transfer capability.
3. A sender chooses one file.
4. `SessionFileTransferService` creates a `FileTransferOfferV2` and sends it over the secure control path.
5. The receiver accepts or declines.
6. On acceptance, both sides exchange `FileTransferSessionOpenV2`.
7. The transport opens an `IFileTransferDataSession` for the session and transfer id.
8. The sender sends a V6 manifest containing file name, size, chunk size, chunk count, and SHA-256.
9. The receiver sends V6 receiver-state frames with committed progress and explicit request ranges.
10. The sender pumps V6 chunk batches only for ranges requested by `receiver_state.v6` or `frontier_request.v6`.
11. The receiver writes chunks, commits progress, repairs missing ranges, and verifies the final SHA-256.
12. The transfer ends with complete, cancel, or error state on both sides.

## Main Components

### Core Contracts And Models

- `src/nLink.Core/FileTransfer/IFileTransferSignalingTransport.cs`
  Defines file-transfer control sends, data-session opening, and data-session receive/send.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.cs`
  Owns transfer state, offer/accept flow, V6 sender and receiver behavior, progress, hard-priority pause/resume/cancel, and completion.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV6.cs`
  Implements the active V6 receiver-driven sender and receiver runtime.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.TransportEpochV6.cs`
  Implements V6 transport epochs for Tuna activation, Tuna fallback, Tuna restart, and regular NKN recovery.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.Liveness.cs`
  Implements V6 heartbeat/liveness and peer-disconnect terminalization.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV4.cs`
  Remains as a legacy/internal compatibility path for older tests and naming debt. Active negotiated V6 transfers use the V6 runtime.
- `src/nLink.Core/FileTransfer/SessionFileTransferModels.cs`
  Defines descriptors, snapshots, flow policies, terminal states, and progress models.
- `src/nLink.Core/FileTransfer/FileTransferProtocol.cs`
  Defines protocol constants and message type names.
- `src/nLink.Core/FileTransfer/FileTransferDataFrameV4.cs`
  Defines the legacy V4 records plus current V6 manifest, receiver-state, chunk-batch, transport-epoch, transport-probe, frontier-request, repair-proof, complete, cancel, error, pause-control, and heartbeat frames.
- `src/nLink.Core/FileTransfer/FileTransferDataFrameCodec.cs`
  Encodes and decodes compact binary V6 data frames, while retaining safe decode handling for legacy tests/helpers where needed.
- `src/nLink.Core/FileTransfer/FileTransferPayloadCodec.cs`
  Encodes and decodes file-transfer control payloads.
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
  Implements control payload routing, data-session behavior, secure envelope validation, V6 data-frame dispatch, transport-origin metadata, lifecycle control payloads, epoch proof messages, and file-transfer diagnostics.
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
- `pause_control.v6`
- `heartbeat.v6`
- `transport_epoch.v6`
- `transport_probe.v6`
- `repair_proof.v6`

These messages stay on current NKN even when Tuna is active.

### Data Frames

V6 data frames use `IFileTransferDataSession`:

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
- Default V6 chunk size: `21 KiB`.
- Default maximum V6 batch segments: `3`.

Payload efficiency profiles are available for focused tuning:

- `current`
- `packed_3x20kib`
- `packed_3x21kib`
- `large_single_48kib`

The environment variable is `NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_PROFILE`. Mixed screen-share coexistence can be controlled with `NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_ALLOW_SCREENSHARE`.

## Flow Control And Recovery

File transfer is receiver-driven. The receiver reports what it has durably accepted and how much more it can take. The sender pumps only within the advertised budget.

Important V6 receiver-state fields include:

- contiguous committed chunk index,
- durable highest received chunk index,
- explicit missing/request ranges,
- bytes committed,
- memory pressure,
- disk pressure,
- terminal readiness,
- pause state.

Recovery behaviors include:

- missing-range repair,
- sender repair cache,
- sparse receiver writes where the destination stream supports seek/read/write,
- file-only fast repair paths,
- mixed screen-share pressure handling,
- sender pump depth and pending-byte limits,
- sender-side enforcement that chunks overlap an active receiver request,
- pause/resume control from either side.

The transfer treats integrity and terminal correctness as more important than raw throughput.

## V6 Transport Epochs

V6 uses an explicit transport epoch state machine for file-data movement. It is used for both directions:

- normal NKN to Tuna activation,
- Tuna to normal NKN fallback,
- Tuna restart,
- regular NKN receive recovery.

Every transition creates a `FileTransferV6TransportEpoch` with source transport, target transport, reason, starting committed frontier, highest observed chunk, probe id, repair request id, and proof timestamps. Generic bridge readiness, Tuna readiness, send success, or bulk bytes are not enough to declare recovery.

The epoch states are:

- `EpochStarting`
- `TargetProofPending`
- `FrontierRepairOnly`
- `BackfillRepair`
- `Recovered`
- `WaitingForTargetTransport`
- `Terminal`

During `TargetProofPending` and `FrontierRepairOnly`, the sender blocks new/tail chunk traffic on the target transport and sends only target probes or exact frontier chunks requested for the active epoch. Transport send success is logged as sent, not recovered. Recovery requires regular-control acknowledgement of `transport_probe.v6` or `repair_proof.v6`.

The receiver is authoritative for recovery. On an epoch it sends `receiver_state.v6` and, when stalled at the frontier, `frontier_request.v6` for the exact missing chunk/range first. Far-ahead chunks may be stored or deduped when the destination stream supports sparse writes, but they do not prove recovery until the committed frontier advances and proof is acknowledged over regular NKN control.

If proof does not arrive within the recovery window, the transfer remains alive and enters `WaitingForTargetTransport` / user-facing waiting state instead of falsely reporting recovered.

Legacy V5 handoff names may still appear in compatibility aliases or old tests, but active negotiated transfers use V6 transport epochs.

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

Lifecycle actions are hard-priority and local-first. Cancel, pause, resume, session end, peer down, window close, app exit, and transport detach bypass data queues, repair queues, Tuna, bulk backlog, and the V6 transport-epoch state machine.

When a lifecycle action is requested:

- the local transfer card updates immediately,
- transfer lifetime work is stopped or paused locally,
- a best-effort lifecycle notice is sent over regular NKN control,
- lifecycle data-session frames remain codec-supported but are not used as the authoritative Phase 2+ runtime path,
- duplicate lifecycle notices are idempotent,
- late data frames after terminalization are dropped with rate-limited diagnostics.

Complete wins only when checksum/final terminal state was committed first. Otherwise hard lifecycle terminal state wins.

## Diagnostics

The main support surfaces are:

- Options -> Diagnostics -> Copy diagnostics.
- `tools/FileTransfer-Ops.ps1`.
- Retained logs under `%LOCALAPPDATA%\nLink\logs`.
- File-transfer analyzer artifacts.

Useful diagnostics include transfer ids, terminal state, bridge bulk health, file-transfer message rejections, V6 manifest/receiver-state/chunk events, transport epochs, target probe acknowledgement, frontier repair proof, receiver buffer pressure, repair/reorder summaries, coexistence summaries, lifecycle-priority markers, and external transport health summaries.

The first retained-analysis file to read is `filetransfer-operator-verdict.txt`.

## Current Limits

- Single-file transfers only.
- No folder transfer yet.
- No drag-and-drop yet.
- No resume after app restart.
- Default file-size cap is `25 GiB`.
- Live throughput depends on NKN and network delivery.
- Tuna remains experimental and optional. Current NKN remains the default and fallback path.

## Validation

For deterministic local regression checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast
```

For operator flow and artifact interpretation, use [`docs/file-transfer-operability.md`](file-transfer-operability.md).
