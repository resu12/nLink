# Post drain logging RCA and fix plan

2026-10-01. Investigated branch `v0.7.0`, HEAD
`7f0bc969412f0d606fd4560b0bdc3b1fed7255c3`.

The latest targeted transfer completed with matching SHA-256 and the required
Regular V4 → Tuna V4 → fallback V6 → Tuna V4 → fallback V6 route sequence.
Strict acceptance nevertheless failed because the sender attempted one
operational-log write after its final drain. The rejection is correct; shutdown
had not stopped every logging producer before closing the writer.

The acceptance cause is confirmed. The exact late message and originating
callback are not yet identified. This document proposes diagnostics followed
by a producer-specific shutdown fix; it does not authorize a general logger,
transport or acceptance rewrite. No production code or acceptance rule changed
during this RCA.

## Current evidence

The run is `20261001T094106Z`, scenario
`regular-v4-live-activation-off-on-off-256mb`. It used the rebuilt portable
package from the investigated HEAD, a clean worktree, strict preflights and one
attempt without retry. All 59 pins in the
[post-run review](<C:/Users/Juraj/Desktop/Remote help/artifacts/live-checkpoint-7f0bc969-20261001/20261001T094106Z/post-run-review.json>)
were reverified without drift.

Both receiver holds were prepared, captured and drained by accepted coordinator
decisions, at source epochs 1 and 3. Both directions then published fallback V6
and the second Tuna V4 activation. The retained analyzer reports route
consistency and runtime correctness `pass`, no hard failures, and two efficiency
warnings. The GUI reports both transfers completed and a 268,435,456-byte
received file with matching SHA-256. These positive findings do not override
the failed writer-integrity gate.

The [sender failure marker](<C:/Users/Juraj/Desktop/Remote help/artifacts/live-checkpoint-7f0bc969-20261001/20261001T094106Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-gui-log-writer-output-snapshots/001-helpee-9208.stderr.log>)
binds the failure to writer `p9208r1d1ff502e8fe451b`. Times below come from that
writer's attempt ticks, not line ordering across the two processes.

| Boundary | UTC | Writer sequence |
| --- | --- | ---: |
| Session runtime transitions to Disposed | 09:47:12.5877220 | 68758 |
| Last persisted payload line | 09:47:12.5937822 | 68761 |
| Final writer drain | 09:47:12.6040994 | 68762 |
| Rejected one-line write | 09:47:12.6558310 | 68763 |

The failed attempt is **51.7316 ms after drain**, with
`failure_code=writer_completed` and `attempt_count=0`. Its canonical payload
SHA-256 is `4b79c63960a419c3cddc998fec2af95afbca2d4d59481487f6c1a6d6d10b3764`.
The retained ledger has no writer gaps, duplicates, identity mismatches,
chronology inversions or missing drains; it has one terminal write failure.
Zero physical attempts and the explicit completed-writer reason distinguish
this from a disk, mutex, queue-capacity or throughput failure.

## Why acceptance fails

[LocalOperationalLog.Write](<C:/Users/Juraj/Desktop/Remote help/src/nLink.Core/Logging/LocalOperationalLog.cs:55>)
still creates an exact batch for a late write. If a frontier writer is bound,
it also cancels that handoff and revokes the writer registration. The
[write pump](<C:/Users/Juraj/Desktop/Remote help/src/nLink.Core/Logging/OrderedOperationalLogWritePump.cs:154>)
rejects a non-drain batch once completion has been requested and emits the
failure marker to stderr. It must not reopen the final evidence stream.

The [GUI ledger validator](<C:/Users/Juraj/Desktop/Remote help/tools/GuiSmoke-Windows.ps1:4606>)
therefore records `operational_log_write_failure_observed`. The
[direct manifest](<C:/Users/Juraj/Desktop/Remote help/tools/Run-FileTransferTunaGuiSmoke.ps1:2472>)
requires passing cursor and writer integrity, so `currentRuntimeProof=false`.
The [route-acceptance wrapper](<C:/Users/Juraj/Desktop/Remote help/tools/Run-FileTransferRouteAcceptance.ps1:9471>)
then rejects selection of this attempt. The campaign's
`filetransfer_route_acceptance_finalized_attempt_selection_missing` is the
downstream result, not the first defect to fix.

Retained analysis completed with exit 0, unchanged input and passing full/compact
snapshot parity. Its input hash matches the ledger's analyzer-input hash. The
absent derived-input receipt is not a separate failure in this run. The later
payload-wrapper `ObjectDisposedException` is also not established as the
originating cause; do not replace the proven writer failure with that symptom.

## Shutdown gap and attribution limit

[Program.Main](<C:/Users/Juraj/Desktop/Remote help/src/nLink.App/Program.cs:22>)
performs the final drain after desktop shutdown. The desktop exit handler
disposes the main view model, which disposes the session runtime. The
[frontier lifetime](<C:/Users/Juraj/Desktop/Remote help/src/nLink.Core/FileTransfer/SessionFileTransferService.FrontierAppLifetime.cs:93>)
waits on its producer registry before draining, but that
[registry](<C:/Users/Juraj/Desktop/Remote help/src/nLink.Core/FileTransfer/SessionFileTransferService.FrontierProducerRegistry.cs:28>)
tracks coordinator snapshot emitters, not every app logging task.

There are plausible paths outside that barrier. In particular,
[SessionRuntime.RunCountedBackgroundTask](<C:/Users/Juraj/Desktop/Remote help/src/nLink.App/Services/SessionRuntime.cs:674>)
discards the returned task; its counters are not completion joins.
[BackgroundTaskRunner](<C:/Users/Juraj/Desktop/Remote help/src/nLink.App/Services/BackgroundTaskRunner.cs:33>)
can log failure or finalizer errors after its body completes.
[RunBoundedCleanup](<C:/Users/Juraj/Desktop/Remote help/src/nLink.App/Services/SessionRuntime.RemoteControlHost.cs:1859>)
can also return on timeout while its cleanup task continues. No retained
`dispose_timeout` establishes that the latter path caused this run's failure.
These are inspection findings, not identification of sequence 68763.

The current failure marker retains exact batch identity and a hash, but no
source, event name or caller. An offline reconstruction checked 96,536 distinct
retained message candidates with the failed attempt's timestamp and writer
metadata; none matched. That rules out those candidate bodies only. The final
NKN `Disposed` console message is not the missing operational-log line:
`NknSignalingTransport.Log` writes to stdout, a separate channel.

## Bounded fix plan

1. **Identify the late producer without changing acceptance.** Add bounded,
   redacted source and event-name diagnostics to the exact rejected
   `writer_completed` attempt. Bind them to its existing writer, batch,
   sequence and payload hash. Do not copy a full message, stack, path or secret;
   emit through the existing stderr failure path, never recursively through
   the completed operational logger. Missing or malformed diagnostic fields
   must not alter the rejection, persistence receipt or handoff revocation.
   This diagnostic-only slice is the recommended first implementation.
2. **Reproduce the identified lifetime escape deterministically.** Hold the
   actual producer at a barrier, initiate the normal app close path, then
   release it. Establish which owner returns before that task's final logging
   and cleanup. Use an isolated child process where the static logger must be
   completed. A synthetic post-drain write alone tests the logger, not the app
   race. If offline work cannot identify the live producer, rebuild and request
   one targeted diagnostic live run; do not guess a runtime fix.
3. **Fix only the proven owner.** Stop that producer's admission, cancel its
   work and join its exact completion before the final operational-log drain.
   Preserve bounded shutdown and handle timeout as incomplete evidence. Wait
   outside service/UI/logging gates; account for callback dispatch so shutdown
   cannot deadlock. Do not introduce a process-wide task registry unless the
   identified path demonstrably requires it.
4. **Verify the corrected ordering.** The producer's last diagnostic must
   precede the one final drain. Cover normal completion, delayed completion,
   cancellation, producer failure, duplicate close and bounded timeout. A
   deliberate post-drain attempt must still fail and invalidate proof. Run
   only affected logger, producer-lifecycle and direct-manifest regressions.
   Then review, checkpoint, rebuild and rerun this one live-256 scenario with
   strict preflights. Require both writer integrity and the unchanged route
   cycle/SHA contract before a full matrix.

Do not suppress `writer_completed`, ignore post-terminal logs, reopen the
writer, manufacture another drain, waive selected-attempt proof, add a grace
sleep or increase timeouts/queue limits. Do not change route authority, bridge
throughput, payload sizing, wallet policy, GUI setup or unrelated shutdown
owners while investigating this edge.

## Verification performed for this RCA

The read-only [inspection script](<C:/Users/Juraj/Desktop/Remote help/artifacts/post-drain-writer-rca-20261001/Inspect-PostDrain.ps1>)
and [result](<C:/Users/Juraj/Desktop/Remote help/artifacts/post-drain-writer-rca-20261001/inspection.json>)
retain the marker, exact time calculation, candidate-search limit and source
pins. The existing
`OrderedOperationalLogExactBatchReceiptTests.SubmissionAfterWriterCompletionHasAnExactFailedReceipt`
test passed: one test, zero failures. It confirms intentional rejection and
non-persistence after drain, not the identity of the live producer. Its
[TRX](<C:/Users/Juraj/Desktop/Remote help/artifacts/post-drain-writer-rca-20261001/control/writer-completed-control.trx>)
is retained. The initial RCA was read-only; the subsequent diagnostic
implementation is recorded below.

## Diagnostic implementation and verification

The first implementation step is complete in the working tree. Only failed
`writer_completed` stderr markers gain `attempted_source` and `attempted_event`.
Their writer, batch, sequence, attempt time and payload hash remain unchanged.
The write pump, persistence receipts, final-drain behavior, handoff revocation
and acceptance rules are unchanged.

The formatter inspects at most the first 256 characters of the already-redacted
entry. It accepts a complete source identifier of at most 64 characters and
an event identifier of at most 128 characters, only when `event=` is the first
message field and ends with a semicolon. It does not search later lines or
copy message fields, paths or stacks. Each identifier also passes through the
redactor in isolation; missing, malformed, oversized or secret-shaped values
become `(none)`. This deliberately can omit a legitimate long identifier that
resembles a secret. The fields are diagnostic hints, not proof of a specific
callback or runtime authority.

Verification on October 1, 2026:

- The [red run](<C:/Users/Juraj/Desktop/Remote help/artifacts/post-drain-diagnostics-20261001/red/red.trx>)
  had 23 expected failures for absent diagnostics and one unchanged-marker
  control pass before the formatter change.
- The [focused run](<C:/Users/Juraj/Desktop/Remote help/artifacts/post-drain-diagnostics-20261001/focused/focused.trx>)
  passed all 25 checks. These cover identity preservation, bounded extraction,
  malformed and multiline input, redaction, failed persistence and the GUI
  evidence reader. The reader retains the enriched marker exactly and still
  reports `operational_log_write_failure_observed`, without duplicate fields.
- The [guard run](<C:/Users/Juraj/Desktop/Remote help/artifacts/post-drain-diagnostics-20261001/guards/guards.trx>)
  passed all five checks: valid and malformed rejected batches remain failed
  and unpersisted; the legacy marker stays unchanged; and a write after a
  completed native handoff still invalidates proof in both PowerShell hosts.

The live producer is still unidentified; no shutdown fix is claimed. No new
live run, aggregate suite, commit or push was performed. The next step is to
review and checkpoint the diagnostic changes, rebuild the portable package,
and request one targeted live-256 diagnostic run with strict preflights. Use
its exact rejected attempt to identify the producer before changing lifetime
ordering. A run without a recurrence would not establish that the race is fixed.
