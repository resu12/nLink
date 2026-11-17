# Peer-first inbound fallback-tail yield: RCA and bounded fix plan

Date: 2026-09-28  
Branch: `v0.7.0`  
HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Status: diagnosis and plan only; no production, test, or acceptance changes.

## Finding

The inbound peer-first local-yield path omits the frontier-obligation check already present in the ordinary inbound quiescence path. In the latest small offline run, the ordinary path twice refused yield with contiguous frontier 1 and highest received chunk 3. A subsequently accepted peer proof entered the alternate path, which requested and received a coordinator quiescent yield, retired inbound recovery ownership, and paired the yield while that gap remained.

The first incorrect edge is **local admission of the inbound quiescent-yield request despite current missing-frontier evidence**. The later retired-owner suppression and stale-wake rejection must remain fail-closed; they are not permission to restart an already retired worker.

## Evidence and freshness

Authoritative input is the original log from:

`artifacts/frame-hold-implementation-20260928/pwsh-frontier-hold-a8760a38fc81498d9b18a3337df25e81/live-operational-log/nlink.log`

- SHA-256: `1c46a825de2d580951bce4eeed29547dc9e8e8c00d95cd31a2ee748eea8c1260`; 1,591 physical lines.
- All 120 original coordinator transition payloads were decoded as Base64URL JSON and checked against their recorded SHA-256; zero mismatches. This was read-only inspection, not another authority evaluation or a new acceptance witness.
- All 1,165 pins and all 458 dirty-file status/hash entries in the prior implementation seal matched before this report was created. The affected source and captured binaries have not changed since that run.
- The native small-pair payload was 65,536 bytes. The harness-created 256 MiB file does not make this a 256 MiB transfer run.
- The small run failed to complete. It produced no successful native payload-marker/receipt witness and no completed destination SHA. No retained acceptance analyzer, 256 MiB run, or live GUI/NKN test was run in this RCA turn.

Physical line numbers below refer to that original log, not a filtered copy. Cross-process adjacency alone is not treated as causality; the receiver's ordered records and the exact coordinator snapshots establish the relevant transition.

| Original line(s) | Observation |
| --- | --- |
| 974, 979 | Later chunk 3 reaches the receiver while the earlier bulk envelope is held. |
| 1018 | Coordinator-authorized frontier repair for chunk 0 is issued. |
| 1173, 1176 | The released old epoch-0 batch is received and rejected with `fallback_frontier_protocol_alias_missing`. Rejection is not repaired-byte progress. |
| 1210, 1212 | The authorized repair settles as `ProgressObserved`; contiguous progress advances 0 to 1. |
| 1214, 1251 | Ordinary inbound yield is blocked by the frontier obligation: committed 1, highest 3, fallback start 0. |
| 1216 | A current recent-progress recheck is scheduled for frontier 1. |
| 1249 | Outbound coordinator accepts its own quiescent yield, reason `receiver_state_sparse_runtime`. |
| 1279 | Inbound coordinator retains the initial peer proof. |
| 1298 | Inbound coordinator accepts `FallbackTailQuiescent`, reason `fallback_tail_peer_yield`, state generation 51. The pending initial peer proof becomes the current operation's peer proof. |
| 1313, 1315 | Inbound recovery ownership is retired, including the exact snapshot-bound authority effect. |
| 1383–1388 | Inbound pairing, paired release, and `fallback_quiescent` runtime-unlock admission follow. |
| 1391 | The next repair update is suppressed with `fallback_tail_retired_owner_exact_scope`. |
| 1392 | The frontier-1/highest-3 wake is rejected with `route_scope_changed`. |

## Source chain

Line references describe the source pinned for this report.

1. `SessionFileTransferService.cs:16704` resolves inbound survival after accepting a peer proof. At `:17380`, absent a current local yield operation, it calls `TryMarkInboundFallbackTailLocalYieldFromCurrentLegLocked`.
2. That alternate method at `:17576` checks current fallback ownership, send permission, checkpoint, proof replay, and transport handoff. It does **not** check the current sparse frontier or unresolved frontier obligation before calling the coordinator at `:17651`. Its yield arguments use the leg's proven progress.
3. The ordinary method checks `IsInboundPostFallbackFrontierGapMissingLocked` **OR** `IsFallbackFrontierObligationBlockingTailYield` at `:19479`, before requesting coordinator yield at `:19565`.
4. The sparse predicate in `SessionFileTransferService.PullTransferSessionV4.cs:28051` uses current committed frontier, highest received chunk, written bitmap, and pending writes. Merely reusing the settled-contract helper is insufficient: the old chunk-0 repair has settled and progress is past fallback start, while the new chunk-1 gap remains.
5. Coordinator quiescence sets the leg to `Active` in `FileTransferCoordinator.cs:11025`; service retirement at `SessionFileTransferService.cs:18427` clears recovery/handoff ownership. The exact retired-owner guard at `FileTransferCoordinator.cs:12097` then suppresses old-scope repair.
6. The wake guard in `SessionFileTransferService.PullTransferSessionV6.cs:8338` checks the captured route/owner/leg scope, including leg state. The log proves a scope mismatch, but does not identify every compared field; attributing that rejection exclusively to leg state would overstate the evidence.
7. `SessionFileTransferService.TransportEpochV6.cs:7154` also invokes the alternate local-yield method during authorized transport-epoch reauthorization, with `allowCurrentTransportEpochRecoveryHandoff: true`. That flag must not silently bypass a frontier-obligation veto.

The outbound early yield is not changed by this plan: existing moving-frontier tests intentionally permit it after repair progress. Whether another downstream proof edge appears after fixing inbound admission remains a question for the targeted rerun, not grounds to widen this fix now.

## Bounded implementation plan

1. Add a focused regression for the inbound peer-first path: current fallback-v6 scope, no pending checkpoint/replay, settled chunk-0 repair, committed 1, highest 3, and chunk 1 missing in the sparse bitmap. Deliver a current exact peer proof through the existing service entry point.
   - Verify the peer proof can be retained as evidence, but no local yield, local-proof dispatch, paired release, recovery retirement, or runtime-unlock admission occurs.
   - Verify current recovery ownership is preserved. A diagnostic refusal must not mutate route authority.
2. Before the alternate path requests coordinator quiescence, apply the same current sparse-gap OR unresolved-frontier-obligation veto as the ordinary inbound path, using current receiver progress.
   - Keep all coordinator decisions, currentness checks, and existing retirement rules authoritative.
   - Reuse the existing frontier-obligation diagnostic. Do not introduce a timer, new authority DTO, retry fallback, or acceptance exception.
   - Keep the handoff exception separate: allowing the current reauthorization handoff is not permission to ignore missing-frontier evidence.
3. Verify deferred progress, not just refusal.
   - When a fresh current observation closes the gap and settles the obligation, the existing ordinary quiescence path may request a new coordinator decision.
   - The coordinator already carries an exact pending initial peer proof into an issued local operation (`FileTransferCoordinator.cs:14372–14428`). Verify exact pairing occurs once; do not manufacture a second peer proof or accept an obsolete scope.
   - Add coverage for an unsettled frontier contract and for reauthorization with the handoff flag both enabled and disabled.
4. Run only the directly affected tests, then one small offline `OriginalApps_CharacterizeFrontierHoldOnSmallPair` rerun after rebuilding its harness. Keep its existing deadlines and frame-hold settings.
   - Success for the regression is no premature inbound yield with the gap present, followed by legitimate continued progress.
   - Success for the small rerun additionally requires normal completion and the existing native observer/witness requirements. Passing the unit regression alone does not establish those witnesses.
   - If a different first proof edge blocks completion, retain the new artifact and diagnose that edge before another runtime change.

### Existing regression coverage to retain

Use exact test names rather than a generic aggregate lane:

- `PostTunaFallbackV6ReceiverStateProof_RetiresInboundCheckpointOwnerAfterSend`: ordinary inbound missing-gap refusal.
- `InboundPostTunaFallbackMovingTailProgress_YieldsAndResumesRuntimeUnlockResponder`: legitimate ordinary quiescence.
- `PostFallbackPairedRetryOffer_CheckpointAcceptanceRefreshesExactPublishedScope`: peer-driven local yield and exact published checkpoint scope.
- `InboundPostTunaFallbackRecoveryRestartAfterTransportEpochAdvance_IssuesExactYieldSuccessor`: authorized successor behavior.
- `InboundPostYieldFrontierStallState_IsSuppressedBeforeControlSend`: no stale repair revival after a valid retirement.

The existing `PostTunaFallbackV6PeerFirstYield_RechecksAfterFrontierAndStateRefreshClear` fixture is **outbound**; it does not cover this inbound bypass. Existing no-gap fixtures without a written bitmap also do not reproduce the observed missing-frontier state.

## Boundaries and next gate

No runtime/test implementation, build, test execution, live-network call, analyzer run, commit, or push occurred in this turn. Only this RCA report and ignored diagnostic/audit artifacts were added. Existing work and acceptance rules remain unchanged.

Next step: implement the regression and admission veto above. The held 256 MiB characterization remains blocked on a passing small-pair prerequisite; no live 256 or full matrix is authorized by this report. Do not change chunk size, queue/concurrency, throughput policy, retry cadence, timeouts, protocol invariants, GUI behavior, or stale-callback authority to force a pass.

Evidence and preservation audit: `artifacts/peer-first-frontier-yield-rca-20260928/`.
