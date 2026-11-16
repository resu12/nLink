# Broader offline frontier-fault contract review

Date: 2026-09-28  
Branch: `v0.7.0`; HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Status: review complete; conditional small-profile proposal. No implementation or run authorized by this document.

## Decision

More opaque holds alone do not resolve the current batch-overlap problem. A source-backed alternative schedule can be described using **one chunk per original frame**, one deliberate loss and three bounded receive-side holds.

That packetization is an **explicit exception still requiring approval** to the current no-batching-change constraint. It changes the test process's runtime configuration, although production defaults, chunk size, authority rules, clocks and acceptance remain unchanged. It must not be described as validation of default three-segment batching or as permission for a live run.

The proposed first milestone is a genuine original-pair **closed native observation and writer handoff**, not guaranteed primary-failure eligibility. The full same-transfer diagnostic witness and live-256 gates remain separate and unmet.

## Unchanged-packetization alternative rejected

Receiver rescue does widen naturally from 1 to 3, 12 and 32 chunks after repeated requests (`SessionFileTransferService.cs:354`, `PullTransferSessionV6.cs:7624`). This initially looked sufficient to repair the entire ordinary batch 0-2 before releasing a distinct batch 3-5.

The actual sender path defeats that assumption:

- `PullTransferSessionV4.cs:14786` projects a real frontier request into a V6 receiver-state-shaped input, preserving its priority and recovery mode.
- `ShouldClampOutboundV6HandoffFrontierRepairRangeForSendLocked` at 14368 exempts explicit backfill/current checkpoint-window cases, not an ordinary post-Tuna frontier rescue.
- `ShouldPreserveOutboundPostFallbackSparseFrontierBackfillRangeLocked` at 14440 explicitly refuses ordinary V6 receiver-state inputs.
- The ordinary rescue uses priority `frontier`, not the explicit `backfill` case.
- The enqueue path at 15705 applies `ClampV6HandoffFrontierRepairRangesForSend` at 14321, reducing that range to the one-chunk emergency frontier.

Therefore a wider receiver request is not proof that a three-chunk response exists. Do not discard repeated repair packets hoping a larger response appears, force a backfill/checkpoint state, or change this runtime clamp to accommodate a test. No defect in the clamp is established by this design review.

## Proposed narrow exception and scope

Use a new, explicitly named **small offline Debug-only** profile, with all of these departures stated up front:

| Item | Proposed profile | What remains unchanged |
| --- | --- | --- |
| Packetization | Set existing `NLINK_FILETRANSFER_V4_MAX_BATCH_SEGMENTS=1` only inside the isolated native test process; restore its prior value in cleanup | Production default remains 3; no batching source constant or chunk-size change |
| Payload | 8 chunks of the existing 21 KiB size: 172,032 bytes (168 KiB) | Real source, hashing, native destination, original service/session/transfer and route sequence |
| Fault boundary | After a complete authentic pipe frame is read, before normal application delivery | Original encrypted NKN envelope bytes and production decoding/validation |
| Loss | Drop exactly the first ordinary, non-repair, single-chunk frame for chunk 0 | No repair/control/bootstrap/receiver-state packet loss |
| Holds | Hold exactly one original ordinary frame each for chunks 1, 2 and 4 | No arbitrary fault sweep, duplicate injection, logical chunk splitting or payload rewrite |
| Limits | At most 3 retained frames, at most 1 MiB aggregate retained bytes; existing 1 MiB per-frame cap; existing 3-second hold deadline per frame without renewal | Existing carrier overall and route-edge deadlines, runtime timers and publication bounds |
| Result | Characterize native closure/custody honestly; failure remains failure | No positive primary diagnostic, route-cycle acceptance or live dispatch inferred |

`ReleaseOverridePolicy.cs:30` permits existing unsafe overrides in Debug; Release requires developer mode. **Do not enable developer mode or weaken that policy.** If the selected assembly/profile cannot use the existing setting, refuse the profile. The setting must be applied/restored in the original test child, not persisted globally or inherited by unrelated GUI/live runs. Verify actual frame geometry, rather than trusting the environment value.

The eight-chunk size is proposed only for this separate small profile. Do not alter the guarded 256 MiB source, canonical-fallback fixture, existing one-shot characterization, or production GUI launch path.

## Concrete conditional packet schedule

All indices are zero-based. First establish the existing real Regular-v4 -> Tuna-v4 -> fallback-v6 transition and arm the test fault only after both original peers have published fallback. Then release the source.

| Stage | Test delivery action | Required original-service result |
| --- | --- | --- |
| 1 | Drop ordinary chunk 0. Hold ordinary chunks 1, 2 and 4. Deliver ordinary chunks 3 and 5-7 normally. | Frontier stays 0; genuine sparse highest reaches 7. All held chunks must be requested/admissible, not fabricated. |
| 2 | Pass every genuine repair/control copy unchanged. | Production requests repair 0, admits the original observation and accepts one repair copy. Frontier advances 0 -> 1; current repair range is exactly [0,1). Duplicate copies remain subject to settled-contract rejection. |
| 3 | After original recent grace is armed and receiver-state tracking reflects frontier 1/highest 7, release original chunk 2 promptly. | A fresh below-highest write commits no contiguous progress. The actual runtime must emit `filetransfer_v6_receiver_state_deferred; reason=frontier_stalled` during recent grace. |
| 4 | Wait for the real recent-grace callback and original observation's rechecked stall root. Release original chunk 1 immediately while the original deadline has not elapsed. | Chunk 1 plus already-written 2 and 3 advances frontier 1 -> 4. The real write must occur before the original root + 750 ms; release time alone is not proof. |
| 5 | Keep original chunk 4 held; do not delay or force receiver-state/control publication. | Frontier 4 remains nonterminal with a real gap and highest 7. The same service captures/binds/publishes its state and closes its observation through an accepted coordinator completion within the existing bound. |
| 6 | Only after observing the original closed-positive status, release original chunk 4 once. | Frontier advances 4 -> 8; original transfer completes with matching SHA, followed by existing app-lifetime marker/drain/adoption/handoff. |

Why the shape differs from the failed capture: chunks 1, 2 and 4 are now distinct **originally produced** frames; none overlaps the [0,1) repair range. Delivering chunk 2 is a fresh sparse write, not a duplicate. Keeping chunk 4 pending prevents the chunk-1 write from terminally completing before receiver-state publication.

This is a source-compatible **candidate schedule**, not observed execution or a guarantee. Abort as unavailable if the actual geometry, identity, repair range, receive window, epoch, state-send cadence or ownership differs. Do not adapt the schedule by dropping more packets, selecting another repair episode, resetting admission, changing a timer or increasing a bound.

## Scheduling seam and completion semantics

The carrier currently sees encrypted bytes, not chunk identities. A new bounded **test-only metadata correlation seam** is needed; a size/ordinal guess is insufficient.

- At the existing NKN envelope construction boundary, an opt-in internal observer may copy only immutable scheduling facts from the actual original data frame: original session/transfer binding, ordinary-versus-repair classification, start/count, plus exact encrypted-envelope length and hash. It receives no mutable context or authority token and returns no runtime decision.
- The test sender associates those facts with the actual outbound bytes and carries the bounded hint in the test pipe's framing/side channel. Receiver matching requires the same peer/process binding, length and envelope hash. The hint is never passed to Core as authority. Missing, ambiguous, foreign, overflowed or unexpected selected-frame metadata fails the profile.
- Register at most eight ordinary-frame descriptors for this eight-chunk profile, each at most 1 KiB, with one-shot consumption and no unbounded history. Include retained descriptors in the aggregate memory bound. Repair/control traffic does not need a scheduling descriptor and remains untouched.
- Keep this seam separate from coordinator authority and diagnostic receipt/Tuna trace classes; do not consult those as transport-decision sources. No test decryption, reserialization, mutation or copying of a private runtime context.
- A real pipe write/flush can finish before the receiver deliberately drops/holds application delivery. That is the proposed network-fault model, not an invented send success. Sender pump execution can continue. No fake peer/application acknowledgment, accepted control-plane completion or publication result may be created.
- The receive loop must continue to read nonheld frames; it must not await a future release while holding the pipe reader or delivery lock. Releasing a retained frame invokes the same normal delivery callback once, with original bytes, only after the existing peer/lifetime checks.
- Read-only original-context observations under the existing service gate may guide release. In particular, `V6LastReceiverStateSentUtc` is assigned before asynchronous delivery (`PullTransferSessionV6.cs:6012`): it is a scheduling hint, **not proof that publication succeeded**. Final original records and the production observation determine whether the episode occurred.
- An unexpected duplicate ordinary selected frame must not slip past a hold or silently consume another drop. Fail the profile. Genuine repair duplicates retain normal delivery and validation.
- Expiry, cancellation, source/peer retirement, negative observation closure or failed stage checks cancel the characterization and discard retained test buffers. No success-on-timeout, implicit second episode or unbounded background release tasks.

Expected implementation surface, if separately approved: the new test scheduler and focused controls; the test pipe client and narrow profile dispatch/carrier case; a default-off internal NKN metadata observer at the existing envelope boundary. No coordinator, receive/repair algorithm, observer state machine, lifetime/handoff check, analyzer or acceptance edit.

## Verification and honest stop points

1. Focused scheduler tests: disabled exact delivery; selected drop/holds/release order; actual send completion versus delayed delivery; ordinary/repair distinction; metadata/hash/peer mismatch; duplicate handling; capacity, deadline, cancellation and cleanup.
2. Directly affected metadata-seam regression: installed/uninstalled observers cannot alter envelope bytes, selected transport, authority checks or completion outcomes. Verify and restore packetization configuration.
3. Rebuild the native test harness and perform **one small offline characterization**, outside the restricted sandbox as required for this app-owned harness. No portable GUI, live network, 256 MiB run or matrix.
4. Keep the original complete capture. Require actual continuity, real deferred event, real recheck, pre-deadline write, exact accepted completion, closed-positive producer, matching payload/marker and honest writer handoff, plus final SHA and handle/process cleanup. No added evaluator or synthetic source lines.
5. Stop at the first new missing edge. Do not rerun an identical failed schedule or tune production cadence to obtain a positive result.

Even a successful closure may produce `PublishedBeforeOriginalDeadline` and leave **no supported unresolved primary failure**. This proposal deliberately does not delay reverse-direction publication to manufacture one. A positive native observation is not the full diagnostic integration contract: original primary selection, 1-64 exact witnesses, original custody and diagnostic-only accounting remain separate. No milestone is waived, and the live-256 second Tuna-publication requirement is untouched.

## Approval boundary and preservation

The user authorized this broader **review**, not the packetization exception or implementation. Recommended next decision: approve or reject the isolated one-segment small-profile exception and the bounded implementation/one characterization above. If rejected, retain the current negative integration evidence; there is no justified timing-only runtime fix.

At entry, all 1,215 prior pins and all 464 dirty-file statuses/hashes matched the previous seal. This turn adds this proposal and ignored source-evidence/preservation records only. No source/test code, settings, binaries, original log, acceptance or prior contract was changed. No tests, builds, authority/analyzer evaluations, transfers, commit or push were performed. Final checks are under `artifacts/broader-offline-fault-contract-review-20260928/`.
