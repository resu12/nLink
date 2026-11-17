# Sender completion before receiver finalization: RCA and bounded fix plan

## Outcome and scope

Current working-tree code deterministically reports sender `Completed` while the
receiver's destination finalizer is held and no completion acknowledgment has
been sent. When that finalizer subsequently encounters a real destination-name
collision, the receiver fails with `finalize_failed`; the sender receives the
error but remains `Completed`. This is a correctness defect, not just UI lag.

This step adds only a diagnostic reproduction test and this report. Runtime,
protocol, acceptance rules, existing tests, and the preceding Windows lifetime
implementation are unchanged. The two new test cases deliberately remain RED
at the intended not-completed assertion. No claim of all-green validation.

Baseline: branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, 253 dirty files. All 51 pins from
`artifacts/windows-destination-lifetime-20260926/FINAL-VERIFICATION.json`
matched before investigation. This is a reproduction of the current dirty-tree
build, not a before/after comparison against a pre-storage-change binary.

## Deterministic reproduction

`tests/nLink.SmokeTests.Core/SessionFileTransferCompletionBoundaryReproTests.cs`
uses two actual `SessionFileTransferService` instances and the unchanged
`CreateTempReceiveDestination` loopback helper. It does not use the new Windows
guard destination. The transfer is 2,048 bytes, one chunk, with actual negotiated
protocol 4 and `regular_nkn_v4_fast` asserted.

The receiver finalizer signals entry and waits before `File.Move`. The existing
sender sparse-completion test hook independently holds the already-selected
completion transition. After both checkpoints are reached, only the sender hook
is released. The test observes actual sender completion before allowing the
receiver finalizer to proceed. No guessed delay, injected completion frame,
reflected production state, or route-authority substitution is used. Eight-second
timeouts bound checkpoint failures; they are not the evidence of the defect.

Observed sequence (both final-code cases):

1. Receiver sends `TerminalReady=true`, with all bytes/chunks acknowledged.
2. Sender selects terminal-ready completion; destination finalizer enters.
3. Releasing sender inference produces sender `Completed`, receiver `Receiving`,
   destination `IsFinalized=false`, no final file, and zero completion messages.
4. Releasing destination finalization either succeeds or collides with a file
   created by the test at the intended final pathname.
5. Success: exact payload bytes at final path, receiver `Completed`, one complete
   message. Collision: collision file unchanged, receiver `Failed`, no saved path
   or complete message, `finalize_failed` delivered to sender, sender `Completed`.

The final intended assertion rejects the captured sender state from step 3 in
both cases. JSON traces and assertion failures are embedded in the TRX output.
Fixtures use unique directories under the workspace artifact root. Both gates
are released and the prior static test hook is restored in `finally`; the shared
fake-network collection serializes hook use. No user receive directory is used.

The receiver snapshot remains `Receiving` during the held callback, despite the
older test's name referring to `Verifying`. This is a separately observed status
detail, not the cause of premature sender success and not changed here.

## First incorrect proof edge

The first semantic error is **all chunks committed -> terminal-success signal**,
before full-file verification and destination finalization establish success.

Current source witnesses:

- `SessionFileTransferService.PullTransferSessionV4.cs:24156`: final-batch handling
  sends terminal-ready; only at `:24192` does it enter finalization. The paused
  progress flush repeats that order at `:24243`/`:24244`.
- `:25518`/`:25523`: the helper emits `terminalReady: true` with a bounded 250 ms
  best-effort send. `:24371` routes it to V4 state or fallback V6 receiver state.
- `:11939`/`:11943`: sender inference requires the sparse runtime, terminal-ready,
  complete frontier/transport counts, acknowledged bytes, and drained in-flight
  frames. None of these proves hash verification or destination finalization.
- `:10874` selects that inference; `:11067`/`:11076` terminalizes the sender with
  the current sparse-pump install. The existing install guard protects identity
  and freshness, not the missing meaning of the signal.
- `SessionFileTransferService.cs:18877` consumes full-frontier terminal-ready as
  fallback tail terminal proof. `SessionFileTransferService.PullTransferSessionV6.cs:664`
  can terminalize directly from it. Fixing only the V4 pump predicate misses this
  second consumer (source-inspected, not a live fallback reproduction here).
- `SessionFileTransferService.cs:5248` performs byte-count/stream/hash checks;
  `:5412` awaits the actual destination finalizer. Completion is sent only after
  that succeeds, at `:5460` onward. The earlier terminal-ready bypasses this order.
- `:5232` excludes terminal outbound contexts from lifecycle error matching.
  `HandleIncomingErrorAsync` at `:4876` therefore cannot repair the false success.
  The terminal controller separately rejects already-terminal transitions.

Do not fix this by allowing late errors to overwrite terminal transfers. That
would weaken established stale-callback behavior; prevent premature success.
Current-source ordering also permits the signal before hashing, but hash-failure
false success was not experimentally reproduced in this step.

## Test evidence

Artifacts: `artifacts/completion-boundary-rca-20260926/results/`.

| Run | Result | Interpretation |
| --- | --- | --- |
| `reproduction.trx` | 2 failed | Initial fixture expected `Verifying`; stopped before the target assertion. Not completion-race evidence. |
| `reproduction-deterministic.trx` | 2 failed | Both reached the intended not-Completed assertion. |
| `focused-contract-witnesses.trx` | 3 passed, 2 failed | Repeated both reproductions; three directly related existing tests passed. |
| `reproduction-error-delivery.trx` | 2 failed | Final test code also observes actual error delivery in the collision case; both still fail only at the target assertion. |

The initial over-specific status check in the NEW fixture was replaced with the
older test's existing not-Completed receiver check. No existing assertion was
changed. The last edit only added error-delivery observation; only the two
affected cases were rerun afterward. Zero skips in all four runs.

The three passing existing witnesses are:

- `FileTunaV4SparseSender_CompletesFromTerminalReadyStateWhenLifecycleCompleteIsLost`
  (constructed-context predicate test, not an end-to-end lost-packet run).
- `PostTunaFallbackV6TerminalReadyReceiverState_CompletesOutboundTransfer`
  (constructed fallback context and receiver-state application).
- `OutboundSparseWorker_TerminalReadyCompletionSupersededBeforeMutationCannotCompleteSuccessor`
  (stale install cannot terminalize a successor).

They demonstrate why deleting completion inference is not an isolated change:
current tests deliberately depend on it. They do not prove a valid receiver
emission boundary. The retired/skipped diagnostic V6 and V4 receiver-stall tests
were inspected but not counted as execution evidence.

Build/test ran offline in the normal sandbox using `--no-restore`, focused
filters, `-p:WarningLevel=0 -p:UsedAvaloniaProducts=`. The latter disables build
telemetry, not transfer behavior. No aggregate or additional compatibility lane
was needed for this diagnostic-only addition.

## Recommended bounded fix plan (not implemented)

Correct the producer's success boundary first, preserving recoverability and
current route authority. Do not add a delay or use sender/receiver byte totals as
a substitute for finalization proof.

1. **Separate receipt from finalized success.** At both all-chunks-received
   call sites, publish final progress with `TerminalReady=false`, then verify and
   finalize. Only the successful finalization continuation may request publication
   of terminal-ready. Keep its best-effort bound so stalled progress feedback does
   not block the existing lifecycle-complete path indefinitely.
2. **Keep success tied to current authority.** Carry the exact context,
   destination/write-source, and current coordinator-owned runtime/publication
   scope through the asynchronous work and revalidate before each success side
   effect. Physical rename alone, `IsFinalized`, or a cached scalar must not grant
   authority. Reuse existing coordinator contracts where sufficient; if a scoped
   publication command is missing, review that minimal contract before adding it.
   Preserve stale-finalizer diagnostic-only behavior and no pathname rollback.
3. **Cover both consumers and loss ordering.** Retain terminal-ready recovery only
   when it represents post-verification/post-finalization success. Verify V4 pump
   and fallback V6 direct completion. Define notification-failure handling at this
   boundary: a successfully published terminal proof must not be followed by a
   contradictory receiver `Failed` merely because another notification failed.
   Do not silently change the existing delivery-failure contract in this patch.
4. **Convert the repro into durable regression tests.** Remove its dependency on
   entering the defective pre-finalization inference branch; assert no successful
   completion while hash/finalization is held, then drive successful or failing
   release. Preserve the original compatibility assertion. Cover hash mismatch,
   final-path collision, cancellation, session/runtime replacement during the
   await, duplicate/late publication, lost lifecycle complete, and stalled
   terminal-state send. Run focused cases, then only directly affected contracts.
5. **Resume destination-proof integration only after that boundary is green.**
   The original-helper receive proof, exact app/transfer destination handoff, and
   full 256 MiB verification remain subsequent work. This RCA needs no GUI rerun.

Compatibility limitation requiring an explicit implementation decision: moving
the producer signal fixes peers running the corrected implementation. An older
receiver can still emit the same wire bit early; it carries no capability proving
the stronger meaning. Do not claim mixed-version safety. If mixed-version peers
must be protected, prefer failing closed on unqualified terminal-ready at the
sender and requiring existing explicit verified completion, with a separately
reviewed lost-completion recovery/capability contract. A new wire field, unbounded
retry, or permissive fallback is not authorized by this RCA. Merely deleting
inference would trade the proven false success for a known lost-completion gap.

These are correctness changes only: no queue/concurrency, chunk sizing,
throughput, wallet, invite, installer, or screen-sharing tuning. Regular/Tuna
remain protocol 4, post-Tuna fallback remains protocol 6, and the live route-cycle
requirement for a second coordinator-published `file_tuna_v4` is unchanged.

## Handoff and preservation

All 253 pre-existing dirty files and all 51 previous pins are preserved. This
step adds the reproduction test and this report (255 dirty paths total).
Verification is sealed in
`artifacts/completion-boundary-rca-20260926/FINAL-VERIFICATION.json`.
The earlier compatibility failure remains open; the new expected-red cases must
not be presented as passing validation. No portable build, GUI/NKN run, full
matrix, commit, or push occurred.
