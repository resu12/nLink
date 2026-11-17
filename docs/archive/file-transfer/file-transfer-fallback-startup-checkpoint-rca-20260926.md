# Fallback V6 startup checkpoint RCA and bounded fix plan

Date: 2026-09-26. Branch: `v0.7.0`. HEAD:
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Outcome

The remaining lost-completion failure starts before destination finalization.
Actual Service startup creates a coordinator-owned fallback leg in
`CheckpointPending`, with `CanSendData=false`, but never registers a checkpoint
request in the observed transfer. Ordinary receiver-state ranges nevertheless
reach the sender pump because its checkpoint-pending predicate requires a
nonempty request ID. The final terminal-ready frame reaches the sender, but the
leg is still closed and the completion guard correctly refuses it.

This step changes only diagnostic test instrumentation and normalizes the fallback
fixture's route flags. No production code, protocol, completion guard, or acceptance
rule is changed. The bounded startup fix below is not implemented yet.

## Current evidence

The final run is
`artifacts/fallback-completion-checkpoint-rca-20260926/results/fallback-startup-final.trx`:
**2 passed, 1 failed, 0 skipped**. Fallback success and destination collision pass;
fallback `complete_lost` still times out waiting for sender completion.

The failing transfer is
`completion_boundary_a15cb15d9a154bbd9237b8d3f4d349db`. It uses actual sender and
receiver Services, a loopback data session, one 2 KiB chunk, and a held actual
destination finalizer. Lifecycle and data-complete messages are deliberately
dropped with successful send returns. The final fixture advertises
`IsFileTunaActiveForRouteSelection=false` and
`IsPostTunaFileFallbackActiveForRouteSelection=true` on both sides, matching the
existing initial-fallback negotiation test rather than advertising both flags.

Read-only observations are taken under the observed Service's own gate. They do
not change owners, legs, leases, repair contracts, or frames and do not acquire
both Services' gates together.

| Observed boundary | Evidence |
| --- | --- |
| Before sender session-open delivery | Leg generation 2, transport epoch 1, `CheckpointPending`, data permission false, request ID and checkpoint lease null. Runtime owner is coordinator-issued for `post_tuna_fallback_v6`, protocol 6. |
| Initial receiver state | Same closed leg shape; epoch 1, frontier 0, one missing chunk, terminal-ready false, no repair request ID or recovery mode. |
| Actual sender chunk-batch send | Same closed leg; no checkpoint lease, repair decision, or frontier repair contract. Batch has no repair request ID. The checkpoint-pending predicate reports false. |
| Destination finalizer held | Both sides retain that leg shape; neither is completed and no completion message or terminal-ready success has been sent. |
| After destination finalization | Receiver emits epoch 2, terminal-ready true, frontier 1, no missing ranges, and completes. |
| Sender timeout | Sender remains `Sending`; it has applied epoch 2 and frontier 1/1, but its leg remains closed with no request ID or lease. |

There are no observed sender checkpoint/frontier-request sends or receiver frontier
repair-request sends in this one-chunk run. Thus the actual payload here did not
use the separately coordinator-authorized fallback frontier-repair path. This
corrects the preliminary possibility that repair authority explained the send.

## First missing proof edge and source chain

1. `RunOutboundV6SenderAsync` calls `StartOutboundPostTunaRecoveryLocked` during
   startup (`SessionFileTransferService.PullTransferSessionV6.cs:115`). Its core
   starts a coordinator leg in `CheckpointPending` with data permission false
   (`SessionFileTransferService.PullTransferSession.cs:5318`). The receiver does
   the corresponding recovery startup during V6 manifest initialization.
2. That direct startup path does not call the checkpoint queue. The separate
   `StartOutboundPostTunaRecoveryWithSafetyReplayLocked` wrapper calls
   `QueueOutboundPostTunaFallbackCheckpointRequestLocked` only for an existing
   positive live-route epoch (`PullTransferSession.cs:5421`). This reproduction
   starts directly on fallback and has live-route epoch 0; the trace shows no
   checkpoint registration or exchange before data or completion.
3. `FileTransferCoordinator.IsCurrentPostTunaFallbackLegCheckpointPending`
   (`FileTransferCoordinator.cs:10597`) requires a nonempty checkpoint ID in
   addition to the closed pending state. It is false for the startup leg.
   `ShouldAcceptOutboundV6NormalReceiverStateRequestsLocked` admits ordinary
   requests when no transport epoch is unresolved (`PullTransferSessionV6.cs:1922`).
   The pump and dequeue guards at lines 2832 and 3394 use the request-ID-dependent
   predicate, so ordinary data is sent without recovering the closed leg.
4. Ordinary receiver states are not matching checkpoint acceptance proofs. The
   post-finalization state therefore does not change the leg to `RecoveryActive`.
   `TryMarkOutboundFallbackTailQuiescentOrTerminalLocked`
   (`SessionFileTransferService.cs:18842`) requires that recovered state and
   `CanSendData=true`; it refuses terminal-ready while the leg remains closed.

The first missing edge is **startup closed leg -> coordinator-registered,
delivered, matching checkpoint exchange -> recovered leg**, not destination
finalization -> frame delivery. A null checkpoint ID must not imply data permission.

This is not merely a test that manually omitted a leg mutation: the fixture calls
the actual public Service startup/accept APIs and never forces recovery state.
Initial fallback selection is represented by `FileTransferRouteResolverInput`
and `OutboundOffer_LogsRouteSelectedTelemetry`'s fallback case. The production
NKN adapter derives its fallback flag from current-session fallback projection.
However, this is still an offline startup reproduction, not proof of the same
condition during a live Tuna-to-fallback route transition or the GUI matrix.
No pre-storage-fix binary comparison was made, so introduction time is not claimed.

## Bounded fix plan

1. Add focused negative coverage for a closed fallback startup leg with no request
   ID: an ordinary receiver-state range must not dispatch payload or grant data
   permission. Preserve separate exact repair-authority semantics; do not equate
   all closed-leg behavior with an in-flight checkpoint response.
2. Wire direct V6 fallback startup to the existing coordinator checkpoint exchange
   at a manifest-ready boundary before ordinary sender-pump dispatch. Reuse
   `QueueOutboundPostTunaFallbackCheckpointRequestLocked` and its existing
   `TryBeginOutboundFallbackCheckpointExchangeLocked` / coordinator authorization,
   lease, exact response matching, and delivery machinery. Do not force a positive
   live-route epoch, fabricate a request ID on the leg, or set recovery/data flags
   from receiver readiness or byte counts. Validate the exact current
   context/owner/leg/session at registration and delayed delivery boundaries.
3. Keep ordinary payload blocked until the coordinator accepts the matching proof,
   including the pre-registration window. Avoid globally removing the request-ID
   condition from a predicate also used for response matching/reissue; that could
   blur distinct states. Keep the terminal-ready guard unchanged.
4. Verify the no-ID gate, missing/wrong/stale checkpoint proof, duplicate response,
   session/owner replacement during startup exchange, and successful exchange
   using only directly affected coordinator/Service tests. Then rerun the three
   fallback completion cases. Required result: actual checkpoint exchange precedes
   ordinary data; held finalization cannot complete either side; lost complete is
   recovered only by post-finalization terminal-ready on the recovered current leg.
5. Once stable, run the directly related completion contract lane once. Keep
   destination/native-proof integration blocked until the unresolved case passes.
   No broad suite or live matrix is needed for this bounded offline change.

Do not fix this by accepting terminal-ready on a closed leg, using full byte counts
as checkpoint proof, setting `RecoveryActive` in the fixture, relabeling the failing
case as expected success, or tuning timeouts/throughput. Mixed-version completion
safety remains separate. The second coordinator-published `file_tuna_v4` live-cycle
requirement is unchanged.

## Runs and preservation

- `fallback-checkpoint-trace.trx`: invalid diagnostic attempt; an observer used the
  wrong reflected leg property name (`LegGeneration` rather than `Generation`),
  interrupting session-open delivery. Retained but excluded from runtime findings.
- `fallback-checkpoint-trace-v2.trx`: one expected-red lost-completion case with
  the corrected observer and original combined route flags; establishes the same
  ordinary-send/closed-leg chain.
- `fallback-startup-final.trx`: final instrumentation and fallback-only route flags;
  two passing cases and the same one unresolved failure. No unrelated lanes rerun.

All 73 prior pins were verified before this step. Only the pre-existing reproduction
test was edited; the other 258 dirty paths are preserved. This report adds one
dirty path (259 -> 260). The preceding reports and result files are unchanged.
`git -c core.safecrlf=false diff --check` passes. The new seal is
`artifacts/fallback-completion-checkpoint-rca-20260926/FINAL-VERIFICATION.json`.
No production edit, portable rebuild, live GUI/NKN run, full matrix, commit, or
push occurred. Validation is not all green.
