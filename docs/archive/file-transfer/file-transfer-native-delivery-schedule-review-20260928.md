# Native delivery schedule: feasibility review

Date: 2026-09-28  
Branch: `v0.7.0`; HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Scope: source/evidence review only. No runtime, carrier, acceptance or timing changes.

## Decision

**Do not implement a timing-only change or rerun the existing hold.** The saved 64 KiB episode has no independently deliverable original frame for the first post-repair frontier. Selecting a frame more precisely would not create the missing frame.

The current one-shot opaque outbound hold is a useful repair/transfer characterization, but is not an implementation-ready positive native-witness schedule. A broader test-fault contract requires a separate decision. This is a scoped feasibility conclusion about the current profile and capture, not a proof that every possible original-pair schedule is impossible.

## Original evidence

Run: `artifacts/frame-hold-implementation-20260928/pwsh-frontier-hold-bcc409f00e1a47ceabd68fcc4dffaeb0`.

Original `live-operational-log/nlink.log`: 1,886 lines; SHA-256 `6dd502bb75ff075d3af3f4a22dd52b8c64dab919f365fccaaf7e1914f227748c`. Exact selected records are saved in `artifacts/native-delivery-schedule-review-20260928/EVIDENCE.json`.

| Original frame | Evidence | Consequence after repair 0 -> 1 |
| --- | --- | --- |
| Ordinary batch 0-2, 64,512 raw bytes | Rejected at line 1174: epoch 0, no repair alias; sender completion at 1177 | Contains missing chunk 1, but also overlaps the repair-0 range. Cannot be trimmed or relabeled by an opaque gate. |
| Ordinary batch 3, 1,024 raw bytes | Received at 977, before first repair; sender completion at 1179 | Already written. A duplicate is not a fresh noncommitting sparse write. |
| Authorized repair 0 | Sender uses `legacy_sparse_v4_send` at 1204; receive at 1215; duplicate rejected as settled at 1253 | Establishes continuity and frontier 1, but does not supply chunk 1. |
| Authorized repair 1 | Request at 1469, dispatched wake at 1473, progress at 1483, receive at 1486 | Exists only after the original stall deadline; it cannot be scheduled before its own authority/request. |
| Authorized repair 2 | Receive at 1693 | Closes the remaining gap; not a substitute for the earlier required episode. |

The prior RCA's original UTC arithmetic remains unchanged: the next commit was 230.149 ms after the original deadline. No new timing evaluator, primary analysis or authority evaluation was invoked.

## Source-backed constraints

1. **The exercised sender is the legacy sparse V4 pump carrying V6 frames.** The original dispatch stage is `legacy_sparse_v4_send`, not the separate native-V6 pump. `PullTransferSessionV4.cs:19054` builds the batch; `ShouldUseV6SparseCreditEnvelope` produces a V6 frame with the original segments, epoch and repair metadata. `NknSignalingTransport.FileTransferTransportChannel.cs:9085` measures the serialized batch and sends it intact when it fits. Splitting is a production transport-budget fallback, not a scheduling knob.

2. **The hold is send-side backpressure, not independent receive delivery.** `AttemptNknPipeClient.cs:87` awaits the hold before taking the physical writer lock. Other already-scheduled frames can overtake it, but the selected send task remains incomplete. The normal branch of the sparse pump awaits `SendChunkIndicesV4Async` (`PullTransferSessionV4.cs:11838`), which retires its pending sends before returning (18989). Its subsequent repair branch awaits the same send routine (11360). On this path, retaining the normal send can delay repair dispatch even though neither the pipe lock nor read loop is held. The capture is consistent with this: repair 0 is sent only after the original hold releases. This does not assert that coordinator-authorized pump replacement is impossible.

3. **Whole-batch overlap is significant.** `FallbackFrontierAuthority.cs:554` computes overlap with the current contract's ranges before sparse duplicate removal. Any overlap, or a nonempty repair alias, makes the batch contract-bound. Missing aliases are rejected (567); a settled contract is rejected (507). The current contract reference/ranges are not removed merely by recording progress at 1370. Thus retaining the same batch until after repair 0 does not turn chunks 1-2 into a valid independent ordinary frame. A duplicate repair packet also cannot be used as fresh repair authority.

4. **Another repair is not the missing original-data write.** `PullTransferSessionV6.cs:7703` defers the next ordinary frontier request through recent grace and the original stall window. The unresolved-epoch shortcut is a different recovery state, not permission for a test to force it. `FrontierPublicationBoundary.cs:209` also closes an already source-bound observation on another repair callback. The next positive write needs an admissible ordinary frame outside the bound repair range, with unchanged source/session ownership.

5. **The real deferred event needs a fresh sparse write without frontier progress.** `PullTransferSessionV6.cs:5361` routes an all-duplicate batch to retry-state handling and returns. For a fresh noncommitting write, the deferred branch at 5638 is reached only when `ShouldForceInboundV6ReceiverStateDuringSparseFrontierStall` returns false. That predicate (6394) forces a state send for an unresolved epoch, absent prior state, new highest chunk, or elapsed interval/watermark limits. A plausible candidate must fill a previously unseen hole below an already advertised highest chunk, remain within the real accept window and arrive soon enough after actual state publication. Merely replaying chunk 3 or delaying a highest-advancing frame does not establish the event.

6. **The post-recheck write must remain nonterminal.** The receive path finalizes and returns at 5608 before its ordinary receiver-state publication branch. A proposed small-payload layout must leave actual trailing work after the required write, rather than assuming a final SHA produces the missing publication. This is a design condition, not a finding that the existing terminal behavior is defective.

## Minimum requirements for a broader test-fault design

No concrete positive schedule is approved or demonstrated. Before implementing a new seam, a revised design must show all of the following on the actual unmodified frame geometry:

- A real initial loss/delay and authorized repair that advances the frontier while leaving another gap.
- A distinct, already-created ordinary frame containing that next frontier, outside the first repair contract's range. It must remain admissible when delivered after recheck and before the original deadline; a scheduler label is not authority.
- A separate fresh below-highest sparse write that can cause the exact deferred event during recent grace, without forging state-send timestamps or replaying already-written bytes.
- Nonterminal work remaining for the original service's frame capture, coordinator-bound send and accepted completion.
- A delivery hold that does not prevent the repair-producing send path from running. Moving a hold to the receive side would require explicit completion semantics: real pipe write/flush may finish, but neither peer-visible application acknowledgment nor accepted receiver-state publication may be fabricated.
- Exact original bytes and original application/session/transfer ownership, bounded retained bytes/frames, cancellation and peer-retirement cleanup, no locks held while overtaking traffic is required, and no false success on expiry.
- A precise account of whether more than one held frame, a deliberate drop, a different payload layout, or a read-only logical-frame scheduling seam is needed. Each is an explicit departure from the current first-frame one-shot profile, not silently covered by it.

A logical selector by itself addresses neither bundled data nor send-pump backpressure. Do not implement one merely to discover later that there is still no valid candidate. Do not change batching/chunk size, reserialize selected portions, override transport budgets, force repair requests, or clear runtime/recovery state to manufacture this episode.

## Options and recommendation

- **Recommended:** separately review a broader offline test-fault contract against the requirements above, keeping runtime/authority/timing/acceptance unchanged. Its first deliverable must be a concrete viable packet schedule, with any necessary contract departures called out; if none can be demonstrated, stop before implementation or rerun.
- **Alternative:** retain the current capture as repair/transfer evidence and an unavailable-positive-witness control. Keep the positive original-pair witness milestone explicitly unmet. This does not authorize relaxing that milestone or starting live-256.

A new contract decision is required before expanding fault behavior. No positive integration assertion, multi-stage scheduler, arbitrary delay sweep, performance tuning or 256 MiB retry follows from this review.

## Verification and preservation

At entry, all 1,211 prior pins and all 463 dirty-file statuses/hashes matched `artifacts/native-witness-window-rca-20260928/FINAL-VERIFICATION.json`. Source inspection and exact extraction from the unchanged original log were sufficient for this design review; no tests, builds, analyzers, native evaluations or transfers were run.

Only this report and ignored evidence/preservation records are added. Source, test code, binaries, original artifacts and acceptance remain unchanged. Final audit files are beside the evidence. No commit or push. The positive small-pair, 256 MiB, live and full-matrix gates remain unmet.
