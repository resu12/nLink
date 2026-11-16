# Completion boundary implementation and remaining fallback proof gap

## Outcome

The premature sender-success defect is fixed for the tested updated-peer paths:
receipt alone no longer emits terminal-ready before hashing/finalization. A
destination collision or integrity mismatch fails both sides rather than leaving
the sender at `Completed`. Regular v4 and Tuna v4 recover when explicit completion
messages are dropped, using the post-finalization state signal.

Validation is NOT all green. The affected-contract run has **27 passed, 1 failed,
zero skipped**. The remaining case is fallback V6 with both explicit completion
paths dropped. The receiver finishes and the sender receives terminal-ready, but
its leg is still `CheckpointPending` with `CanSendData=false`; the existing tail
completion guard rejects that state. No authority guard was relaxed to pass it.

## Implementation

- Both V4 all-chunks-received call sites send bounded final progress with
  `TerminalReady=false`. The ordinary byte/chunk acknowledgments remain intact.
- `FinalizeInboundTransferWithWriteBindingAsync` publishes terminal-ready only
  after byte/hash checks, successful destination finalization, successful return
  from the lifecycle-complete send, and the existing optional data-frame echo.
  The state send retains its 250 ms best-effort bound.
- This ordering preserves the existing reported-send-failure rule. If the
  lifecycle send throws, no terminal-ready success follows; both sides fail.
  A silently dropped complete whose send returns successfully still permits
  post-finalization recovery. A failed/stalled redundant terminal-state send does
  not reverse a completion already delivered on the ordinary path.
- A small `InboundFinalizationSource` captures the original context, stream,
  destination, session/generation, coordinator-issued runtime owner, publication
  token, leg generation/transport epoch, and optional bootstrap write source.
  It is execution evidence, not newly issued authority. Currentness uses the
  existing `FileTransferCoordinator.IsRuntimeOwnerCurrent` validation.
- Finalization success/failure transitions and success publication recheck that
  source. Direct sends validate at invocation; queued control-plane sends compose
  the exact-source check with their existing pre-delivery validator. Late send
  callbacks cannot consume fallback proof or mutate progress for a replaced
  source. A stale finalizer may finish physical work but cannot publish success,
  a saved path, or a terminal error against its successor.
- Existing private entry signatures used by reflection-based contract tests are
  retained as thin forwarding methods. No existing test was rewritten merely to
  accommodate parameter-count changes.

Production changes are confined to the finalization path, its terminal/state-send
guards, and the new source-capture partial. Coordinator algorithms, sender
completion predicates, wire formats, Windows destination storage, and acceptance
rules are unchanged. No queue, throughput, retry cadence, route-version, invite,
wallet, installer, or screen-sharing tuning was performed.

This is the bounded same-updated-build producer fix from the RCA. An old receiver
can still emit the same unqualified wire bit early. Mixed-version safety is NOT
claimed; a negotiated/fail-closed completion contract remains separate work.

## Verification

Results are under `artifacts/completion-boundary-fix-20260926/results/`.

| Run | Result | Scope |
| --- | --- | --- |
| `boundary-first.trx` | 6 passed | Regular v4: held finalization, success/collision, lost complete, reported complete-send failure, stalled/failed terminal-state send. |
| `boundary-routes.trx` | 12 passed, 1 failed | Adds actual-Service loopback Tuna v4 and fallback V6 success/collision/lost-complete cases, plus hash corruption. Fallback lost-complete times out. |
| `scope.trx` | 6 passed | Held finalizer resumed after session, runtime, publication, leg, destination, or lifetime invalidation. |
| `fallback-lost-diagnostic.trx` | 1 failed | Exact failing case only; records the sender/leg tuple and sent receiver states. |
| `affected-contract.trx` | 27 passed, 1 failed | Stabilized boundary: all 19 new/converted cases plus 9 directly related existing contract cases. |
| `native-guard-service.trx` | 1 passed | Actual Windows guard destination completes through the Service and releases its retained native hold. |
| `fallback-final-guard.trx` | 2 passed, 1 failed | Only the three affected fallback cases rerun after adding the final post-send source check; the same lost-complete proof gap remains. |

Across the final applicable results, 28 distinct checks pass and one remains
failing. The last follow-up edit only guards fallback send completion before
checkpoint side effects; no unrelated lanes were rerun.

The converted reproductions wait on actual finalizer entry and assert absence of
terminal-ready/complete while held. They no longer depend on reaching the removed
early inference branch. The original `Receiver_StaysVerifyingUntilFinalizeSucceeds`
assertion is unchanged and passes, as does the original temporary-file success
case. Bootstrap stale finalization, stale sparse-pump completion, delayed receiver
state after replacement, bootstrap feedback terminal binding, and both existing
terminal-ready consumer witnesses also pass in the final contract run.

The route fixtures use the existing loopback readiness/route setup and assert
actual negotiated protocol/route values. They are deterministic Service tests,
not live Tuna/NKN acceptance or a measured transport handoff. Source-invalidation
cases deliberately mutate a committed fixture to model stale callbacks; those
mutations are test-only and do not grant runtime authority.

Offline tests use `--no-restore` and `-p:UsedAvaloniaProducts=` to disable build
telemetry. No full aggregate suite or GUI matrix was run. The expected-red prior
RCA artifacts are retained unchanged; none are relabeled as passes.

## Remaining first proof edge

In both the focused diagnostic and final contract run, the fallback case records:

- receiver state epoch 1: terminal-ready false, frontier 0;
- receiver state epoch 2: terminal-ready true, frontier 1 (all one chunk);
- sender: `Sending`, remote frontier 1 / chunk count 1, applied state epoch 2;
- current sender leg: `CheckpointPending`, `CanSendData=false`, checkpoint ID null;
- receiver: `Completed`, only after the held destination finalizer is released.

Thus the frame is emitted and applied; this is not the old premature publication
or a missing frame. `TryMarkOutboundFallbackTailQuiescentOrTerminalLocked` requires
`RecoveryActive` and data-send permission before consuming terminal-ready.
The passing constructed-context consumer test explicitly installs those conditions;
this actual-Service startup fallback fixture does not attain them.

Next investigation: trace the coordinator-owned checkpoint/leg transitions from
startup through this last receiver state. Determine whether the fixture lacks a
required real fallback transition or runtime completion lacks a valid terminal
proof path while data authority is closed. Do not promote `TerminalReady`, full
byte counts, or a null checkpoint ID into permission to reopen the leg. Do not
change `CanSendData`, force `RecoveryActive`, or bypass the guard to make the test
pass. A bounded fix/contract decision for that edge is required before claiming
fallback lost-completion recovery or proceeding to destination-proof integration.

No pre-change fallback-loss run was made, so this is not evidence that the new
storage/finalization code introduced that checkpoint state. The supported claim
is the observed remaining gap in the current implementation.

## Preservation and scope boundary

Branch/HEAD remain `v0.7.0` /
`a653d4063070235c71fe211480aeb37863d9b2a4`. All 62 preceding pins matched before
implementation. Four of the 255 pre-existing dirty paths were intentionally edited
(three Service partials and the RCA reproduction test); the other 251 are unchanged.
The previously clean terminal controller, new finalization-source partial, new
scope tests, and this report bring the dirty count to 259. Existing unrelated work
and preceding reports/artifacts are preserved.

The verification seal is
`artifacts/completion-boundary-fix-20260926/FINAL-VERIFICATION.json`.
No portable rebuild, live GUI/NKN run, full matrix, commit, or push occurred. The
second coordinator-published `file_tuna_v4` requirement remains unchanged.
