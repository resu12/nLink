# C1: local carrier handoff lifecycle gate

Date: 2026-09-13. Base HEAD: `ef78e9d7`, with uncommitted A/B1/C0 work.
Scope of this step: executable offline lifecycle review and a bounded fix plan.
No production carrier caller, authority reevaluation, classifier or live test.

Subsequent implementation: the [C1a prepared-payload slot](file-transfer-local-evidence-slot-contract.md)
now has transport and actual retained-parent lifecycle coverage. This closes
only the prepared-byte transport/lifetime slice. Real-view preparation, native
reduced-input identity and independently trusted parent expectations remain
unconnected; the production handoff gate below is still not satisfied.

## Plan and pass criteria

1. Execute the retained-analysis parents with controlled isolated workers. Verify
   normal exits, exact terminal receipt ownership and primary output binding.
2. Compare absent, completed, caught-failure and malformed optional output for
   GUI-success/retained-pass, GUI-success/retained-fail and GUI-failure cases.
   Primary report bytes and retained outcome must remain independent of optional
   output; valid-looking diagnostics cannot rescue a primary proof failure.
3. Place a controlled blocking/failing optional stage after durable primary
   reports, before worker exit. Verify actual timeout, cancellation, receipt and
   artifact behavior. Do not declare this insertion point safe if it changes
   the primary outcome. Test-only time scaling must preserve the production
   wait/exit/receipt control flow; production watchdogs remain unchanged.

The controlled worker's summaries and optional JSON are lifecycle fixtures, not
Snapshot authority, C0 provenance or real-transfer evidence. Tests may assert
the current workflow rejects an unsafe proposed insertion point. Such a passing
regression test is not a passing production-handoff gate.

## Concrete ownership and ordering

`Write-FileTransferCompactRetainedAnalysisArtifacts` owns both completed views.
Its full input is the ordered `Index.AllEvents.RawLine` stream; the compact
Snapshot input is an in-memory Snapshot/effect-only selection. Saved summaries
exclude private publication effects. The full-report path has an invocation-local
exact-input handoff but likewise does not export the C0 request/receipt/input set.

The RouteAcceptance and wrapper retained-analysis parents launch FileTransfer-Ops,
wait for worker exit under the original 1800-second watchdog, then drain output,
persist diagnostics, inspect completed proof artifacts and publish the terminal
attempt receipt. Nonzero exit and incomplete proof remain failures. Timeout first
stops the worker, then writes timeout artifacts and a terminal timeout receipt.
In particular, the timeout artifact writer replaces the operator verdict.

The GUI classification worker likewise publishes its response before exiting;
the parent only adopts it after exit. Its later diagnostic companion is guarded
by GUI failure and does not provide the missing retained-success/failure handoff.

C0 is an output-bounded offline consumer, not a bounded-duration pre-exit stage.
At parent receipt/adoption time the child's private views no longer exist. Before
that time, synchronous projection shares the primary deadline. Neither a saved
summary, `eligible=true`, nor a detached carrier's self-declared hash repairs
that missing producer-lifetime/request binding.

## Executable result: current insertion point is not admissible

`FileTransferLocalCarrierHandoffTests` passed 2/2 host cases. Each executes 36
controlled subprocess cases across both retained parents. Production source is
extracted with the PowerShell AST, not replaced by a hand-written parent model.
Only the test watchdog (1800s to 2s), poll interval (5000ms to 25ms), and hidden
test process window setting differ. The original watchdog value is asserted.

Absent/completed/caught-failure/malformed optional output preserves all nine
tested primary report files byte-for-byte within each outcome. Retained proof
failure remains `proof_incomplete`, including GUI success. The `AnalyzeRetained`
entry point exits zero for a returned failing gate unless `FailOnGate` was
requested; the parents do not pass that switch. Thus these lifecycle controls
exercise the actual expected zero-exit/failed-proof distinction.

A controlled nonzero exit after the same primary reports produces a `failed`
terminal receipt, even when proof artifacts pass. A controlled stall after the
reports produces `timed_out`, changes the operator verdict to `INCONCLUSIVE`,
and retains `incomplete_proof`. The worker is stopped before the terminal receipt;
its planned later write never occurs and its process no longer exists. Receipt
output hashes match the finalized artifacts; a second terminal receipt is
rejected without changing the original bytes.

These are counterexamples to pre-exit primary preservation, not runtime defects
to "fix" by accepting a live worker's reports. The current strict parent behavior
is correct. See `artifacts/publication-handoff-review-20260913/RESULTS.md`.

## First missing edge and bounded fix plan

The missing edge is **producer-owned completed views and exact original inputs
to a parent-owned, request-bound detached export**. C0's existing reader comes
after that edge; adding a sidecar writer or another classifier first would not
close it. The following constraints govern the next export design:

1. The retained parent creates the export request and pins its attempt/run/
   scenario, writer input and output namespace. C1b's 2026-09-14 user-approved
   revision replaces preselected uses with a fixed primary-reported-failure
   witness policy; exact lines are selected by the primary. Bind
   this request through the actual primary invocation; do not accept a request
   identifier generated only in an optional response. The current attempt uses
   GUID N formatting, whereas C0 requires canonical D; an explicit same-GUID
   mapping is necessary, not string equality or a new identity.
2. Capture the producer's completed full/compact view association while both
   are alive. Preserve the exact reduced stream and its own physical offsets.
   The current in-memory reduced stream has no file-byte descriptor; a new
   export must provide that original frozen input or explicitly revise the
   carrier input-domain contract. Do not relabel canonical-LF hashes as file
   hashes, map offsets to retained lines, or rebuild historical private rows.
3. Reuse already-built primary indexes for a selected bounded projection. Do
   not call C0's whole-input A/B1 index constructors inline, copy an unlimited
   private Snapshot graph, or run a second authority evaluator. Pin the exact
   fields exported by the primary producer and independently compare full and
   compact rows before claiming a prepared carrier. A producer-export contract
   must be reviewed before replacing C0's completed-view/file-input premise.
4. Establish a resource/lifetime mechanism that does not make primary exit wait
   for optional scanning, IPC, serialization or I/O. Bounded bytes alone are
   insufficient. Failure, saturation, slow I/O or cancellation must omit the
   whole optional export. Primary watchdog, exit checks, receipt status and
   artifact freeze remain unchanged. Do not let a detached child outlive its
   owner's cleanup or continue writing after the optional freeze boundary.
5. Only the parent may adopt the optional export, after its unchanged primary
   terminal receipt and with the separately trusted request/input/payload
   bindings. Use a separate optional artifact namespace; never overwrite any
   receipt-hashed primary file or substitute diagnostics for strict acceptance.
   Define adoption for finalized failing proof as well as successful proof;
   timeout, absent exit and partial primary publication cannot grant eligibility.

Before wiring, repeat the lifecycle cases with that actual export mechanism,
not this controlled optional stage, and add startup/IPC/serialization failures,
resource saturation, deadline-near completion, partial payloads, wrong request,
cross-attempt responses, altered inputs and post-freeze callbacks. Use generated
A/B1/C0 evidence for positive row claims and preserve existing primary artifact
bytes, statuses and evaluator call counts. The current tests deliberately do
not claim descendant-process cleanup, real optional export timing or positive
production adoption.

This review does not authorize weakening primary exit/receipt rules or moving
the original deadline. The exporter/lifetime design is the next bounded offline
step. No new live 256 MiB test can establish the missing export binding by itself.
