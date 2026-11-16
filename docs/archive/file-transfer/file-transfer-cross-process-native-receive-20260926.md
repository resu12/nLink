# Original-process 256 MiB receive: implementation and next-edge RCA

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-native-destination-handoff-20260926.md`.

## Scope and implementation

The explicit offline attempt fixture now connects the original helpee and
helper processes using the existing DevLocal named-pipe transport, production
wire codec, SessionRuntime handshake/approval, and app-created transfer
Services. The original GUI-held 268435456-byte source is consumed first for
metadata and then by the complete V4 payload pump. The original helper uses
SessionRuntime's actual authorized destination factory and retains the native
read-only destination through the existing completion handoff.

The GUI duplicates reduced QUERY_LIMITED_INFORMATION/SYNCHRONIZE handles from
its already-owned process handles into the exact original children. A bounded,
one-shot stdin packet binds each peer handle to its request, PID, and creation
time. No PID-based process reopen is used. Both ends verify the actual named
pipe endpoint against that still-live native process handle. Copies, repeats,
wrong request/creation, a closed handle, and a pipe connected to another process
cannot supply peer identity. These are test-only channels, not route authority.

The initial development run failed before transfer because a single-process
invite helper was incorrectly called in the helper process. The helpee had no
matching issued-secret record. Issuance now occurs in the original helpee;
the token crosses a separate, current-user-only, native-peer-checked local
pipe with a 16 KiB bound. The helper uses the normal input resolver. The
helpee still authenticates and consumes the invite during the real handshake.
No shared security-store workaround or legacy-signature override was added.

The receiver independently SHA-256 hashes the entire finalized file through
the original retained native handle, checks identity, exact length, EOF, and
the Service's completed tuple/hash. Path deletion is denied while custody is
held; the evidence reader is one-shot, and exclusive file access is checked
after release. The sender checks full metadata and payload byte counts and
waits for destination verification before releasing its original source.
No full-file byte array or pathname reopen is used for the destination hash.

## Observed result and first missing edge

The full data transfer and native destination hash succeed. Both Services
report completed protocol 4 / `regular_nkn_v4_fast`, with 268435456 bytes and
the held source's SHA-256:
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.

The **whole attempt is not yet passing**. After normal original-app shutdown
and native log adoption, the retained log processor fails with
`stream_source_not_current` before the two-writer custody join completes.
The first corrected-invite run produced a 40815800-byte log, while only about
2 MB of retained/writer output had been produced at rejection. This is a new
evidence-processing edge; SHA success is not a substitute for it.

The processor's `GuiSmokeLedgerStreamRead.CheckCurrent` enforces a five-second
wall-clock bound. Its supplied `FrontierSourceRead.CanRead` also traverses the
original finalizer/launch-owner/sealed-file scope into the adopter, which has
its own shared, cumulative five-second work budget. Processing must fit in
the remaining adoption/seal/read budget, not receive a fresh allowance.
The fixture now records the original cumulative elapsed time and source size
on this failure without resetting clocks or changing the outcome.

Both final positives reproduce the same first boundary: 5001 ms cumulative
work for a 54648-line source (40814305 bytes under Windows PowerShell 5.1 and
40814301 bytes under PowerShell 7). All native transfer/hash assertions finish
before that rejection; the end-to-end tests remain failed, not waived.

A diagnostic-only replay of the final PowerShell 7 log through the same
processor with an ordinary read-only FileStream visits all 54648 lines in
474 ms, with zero writer gaps/duplicates. It uses bookmark zero and does not
run original ownership validation, retained-output adoption, or the final
writer match. It therefore does not establish custody or acceptance. This
narrows the performance problem to the native owned-source path rather than
generic parser throughput; it is not a per-method profile or a timing SLA.

Repeated full ownership traversal for line reads, buffer fills, and origin
entries is the leading hot-path hypothesis, not yet a measured per-method
breakdown. In particular, `OwnsFrontierBinding` calls both frontier-scope and
sealed-file ownership checks, which revisit the launch, native app/channel,
registration, and adopted-file checks. No profiling claim is made beyond the
boundary measurement and generic-versus-owned replay comparison.

## Bounded fix plan for the new edge

1. Starting from the completed generic replay comparison, profile the
   owned-source processing path against a size-matched synthetic fixture.
   Separate parser/output
   cost from repeated native ownership checks. An artifact-path replay is
   diagnostic only; it cannot recreate original custody or acceptance.
2. Remove only demonstrated duplicate work within a single synchronized
   validation operation. Preserve original-object checks, process incarnation,
   native identity, cancellation, lock ordering, and validation at I/O and
   result-publication boundaries. Do not cache authority across operations.
3. Keep the five-second cumulative budget, all byte/line/origin limits, and
   one-shot retirement unchanged. Add focused regression coverage for the
   measured hot path plus expiration, mid-read cancellation/closure, copied
   owners, and stale completion. If the unchanged contract cannot accommodate
   the bounded workload, present that evidence for a separate contract review;
   do not silently extend a timeout or suppress transfer logs.
4. Rerun only the two full-size offline positives after the bounded fix, then
   the directly affected ledger/source/writer-matching contract lane once.
   A passing run must include both native data/hash checks and the original
   closed-writer/source join. No live GUI/NKN rerun is needed for this edge.

## Verification and preservation

Final verification results and pins are recorded in
`artifacts/frontier-cross-process-receive-20260926/FINAL-VERIFICATION.json`.
Development failures are retained, including the initial invite-fixture error.
The final focused lane has 10 passes (eight native-peer rejection cases and
two default-loader isolation cases), two end-to-end failures at the unchanged
ledger budget, and zero skips. The one directly affected compatibility lane
passes all eight cases: two existing source-channel isolation cases and both
hosts' prior payload-reader, native-destination, and writer-join positives.
Thus this step has 18 distinct passing tests and two unresolved failing tests;
the internal successful data/hash assertions are not counted as extra passes.

The entire preceding 587-pin seal matched before work began; the starting
dirty-worktree inventory contained 269 files. Six pre-existing dirty test
files change intentionally; all other 263 retain their bytes and status.
Two new test/support sources plus this report bring the count to 272. Four
prior seal pins change intentionally and the other 583 remain unchanged.
Build and whitespace checks pass. Only the bounded test harness and this
report are changed in this step; no production source is changed. A local
test-variable naming collision was corrected before the final focused build.

The GUI/parent still does not adopt the destination evidence. Parent analysis
and reconciliation remain the explicit fixture stand-ins. Events/stdout are
test scheduling and assertions only, never receipt or acceptance authority.
No live-256 readiness is claimed: the second coordinator-published
`file_tuna_v4` route remains mandatory for route-cycle acceptance.

No portable rebuild, live GUI/NKN run, full matrix, commit, or push is performed.
