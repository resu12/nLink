# Native sender source reuse across pump installations RCA and fix plan

2026-09-30. Branch `v0.7.0`; implementation base HEAD
`f42e2548f4c331e6f91f00e59894b3683e5e5782`.

The latest targeted live-256 attempt failed immediately after the first Tuna to
fallback transition. An offline regression now confirms a compatibility defect: a new
sparse sender pump can reuse a native source stream whose read authority belongs
to the preceding pump installation. The live sequence strongly supports this
mechanism, but the exact stream reference and rejecting native guard were not
captured in that live attempt. The working-tree fix passes the new held-callback
and same-route successor checks. Wider verification is not yet certified.

The bounded runtime fix and regression are implemented in the working tree.
Acceptance rules and production limits are unchanged. No portable rebuild, live
retry, commit or push has been performed for this fix. The verification section
below distinguishes the offline results from the unchanged failed live attempt.

Follow-up status: the five original wider verification failures and the
correlated-bootstrap fixture migrations are resolved in retained offline runs.
The subsequent accumulated review also fixed a stale fallback-cleanup race;
its focused, affected projection and joined-cycle checks pass. See
the [verification blocker RCA](<file-transfer-verification-blockers-rca-fix-plan-20260930.md>)
for the current findings and next gate; the reports below preserve the earlier
native-fix verification history.

## Current evidence

The reviewed attempt is
[20260930T133822Z](<../artifacts/live-checkpoint-f42e2548-20260930/20260930T133822Z>),
scenario `regular-v4-live-activation-off-on-off-256mb`. It used the fresh portable
from the reviewed HEAD, strict preflights and an escalated live run outside the
restricted sandbox. Connectivity, isolation, portable freshness and provenance
passed. There was one scenario attempt and no automatic rerun.

The [post-run review](<../artifacts/live-checkpoint-f42e2548-20260930/20260930T133822Z/post-run-review.json>)
pins 60 original artifacts. All 60 size and SHA-256 pairs were rechecked during
this RCA and match. Its current SHA-256 is
`860080970fcaf3dfe813583fddd68d8e237df977679dc45488b95b5be121c209`.
The retained analysis completed with exit 0, without timing out, in 133.182
seconds. Snapshot parity passed; current runtime proof remains false.

The following line references are in the original
[operational log](<../artifacts/live-checkpoint-f42e2548-20260930/20260930T133822Z/regular-v4-live-activation-off-on-off-256mb/live-operational-log/nlink.log>).
Times are UTC. Sender writer identity is `p3200r86140119502d4bf7`.

| Time | Observed event | Evidence |
| --- | --- | --- |
| 13:39:29 | Regular V4 pump installation 1 succeeds. | Lines 506 and 516; sender sequences 289 and 299. |
| 13:39:55 to 13:39:56 | First coordinator-published Tuna V4 route appears on both peers. Sender pump installation 2 succeeds. | Sender route sequence 15490; receiver route sequence 7234; pump lines 22642 and 22644. |
| Before fallback installation | Callback lease 2630 is admitted, captured by the source hold, then consumed as Held with allowed effect none. | Decoded snapshots at lines 26478, 26619 and 26627; sender sequences 18599, 18611 and 18612. |
| 13:39:58 | Both peers publish fallback V6. Sender installation 3 replaces installation 2 with reason queued_sparse_runtime_work. | Route sequences 18715 and 8352; pump lines 27018 and 27019, sender sequences 18722 and 18723. |
| 13:39:59 | Sender fails with read_failed and native_sender_source_not_current. Installation 3 supervisor stops as current_pump_failed. | Lines 27575 and 27598; sender sequences 19104 and 19127. |
| 13:39:59 | Both peers reach Failed terminals on fallback V6. | Lines 27594 and 27632. No second Tuna publication is observed. |

The callback snapshot payload hashes were independently checked before decoding.
The held callback fingerprint is `fte1-d106052eb0da76fc6f3e0d82`. These snapshots
prove the callback disposition, not the identity of the retained Stream object.

The initial precommit probe has positive exact-wire evidence through sender IPC,
both sidecars, receiver IPC and application dispatch. The previous attempt's
initial probe delivery gap did not recur in this attempt. The bounded trace has
786 records and two incomplete-stage markers; it does not prove complete capture
or nondelivery of later absent records.

## Root cause and remaining uncertainty

### The source and pump lifetime contracts disagree

[NativeSenderSelection](<../src/nLink.Core/FileTransfer/SessionFileTransferService.NativeSenderSelection.cs#L102>)
opens each payload view from the original retained native anchor. It captures the
current runtime owner, route publication, pending pump installation and data
session, then revalidates that tuple after native I/O. A successful new payload
view must have a newer installation generation.

[RuntimeRead](<../src/nLink.Core/FileTransfer/SessionFileTransferService.NativeSenderSelection.cs#L189>)
retains that tuple and checks it on access. Its installation must still be the
current installed or pending descriptor. This is an intentional safety boundary:
a readable file handle is not authority to read for a successor runtime.

The pump supervisor has two paths that clear its completed task while retaining
the stream: a
[nonterminal bootstrap callback completion](<../src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV4.cs#L3088>)
and ordinary pump completion while the receive loop stays alive. The current log
contains the held callback sequence, but no ordinary
`filetransfer_v4_receive_loop_kept_alive_after_sender_pump_completed` event.
Therefore the held-callback path is the primary reproduction target; successful
pump completion must not be presented as an observed fact.

When queued work requires a successor, the
[queued work branch](<../src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV4.cs#L3000>)
requests reopening only if the retained stream is null. The install helper also
forces reopening for a still-running predecessor, but that condition does not
apply after the task slot has been cleared. The
[installer](<../src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV4.cs#L1414>)
can consequently commit a new descriptor and start its pump with the old nonnull
stream, without invoking the native source factory.

This is the source-confirmed incompatibility. After the new installation commits,
the old native view cannot satisfy its captured installation contract. The
successor pump can nevertheless be current, so its read failure is processed as
a current pump failure. This is not evidence that stale callback authority checks
should be relaxed or that all read failures should be ignored.

The first missing proof edge is: **the current fallback pump installation must
receive a native source view bound to that same installation**.

### What the live evidence does not establish

`native_sender_source_not_current` is shared by multiple native guards. The run
does not record the exact failing guard, source-open count or old and new stream
object identity. The held-callback retention and reuse explanation is strongly
source-supported for the historical live run. The new regression deterministically
reproduces the same error through the held-callback and queued-installation path,
including direct observation of the reused stream. That does not retroactively
identify the particular guard that rejected the historical live access.

The later GUI deadline failure and
`filetransfer_route_acceptance_finalized_attempt_selection_missing` are reporting
outcomes after the runtime read failure, not evidence that timeout limits caused
it. The analyzer finished and both GUI processes exited normally. Increasing
limits cannot restore a transfer already terminalized as Failed.

The terminal hashing change in `f42e2548` was not reached successfully in this
attempt. It remains unvalidated live. No claim of payload corruption, successful
integrity verification or acceptance is supported by this run.

## Bounded fix plan

### 1 Reproduce the retained native stream handoff

Extend the existing native source and sparse-pump fixtures using actual native
custody and production coordinator transitions. Drive a Tuna pump through a held
bootstrap callback completion, retain its stream with no active task, commit the
fallback successor, and force `queued_sparse_runtime_work` installation.

Verify before fixing: the source belongs to installation 2, installation 3 reuses
it, and the new pump fails with the matching native-currentness error. Assert the
held callback remains diagnostic-only. Do not synthesize authority tokens or
wrap the source factory in a way that hides the production source identity.

The existing
[runtime-read fixture](<../tests/nLink.SmokeTests.Core/FrontierAttemptRuntimeReadTests.cs#L108>)
checks four fresh opens and rejects retired views. Its gated reads cover forced
reopening across transitions, not this completed-task and retained-stream branch.
Use that coverage as a base, not proof that the reuse branch is safe.

### 2 Reacquire the native view at installation admission

Make the smallest private change at the source selection and pump installation
boundary so a native installation-bound view is never reused for a distinct
installation. Reopen through the existing native factory and original retained
anchor under the pending coordinator-issued tuple; preserve the existing
post-await revalidation and commit checks.

Keep ordinary source reuse and non-seekable behavior unchanged. Do not reopen by
pathname, mutate an old view's captured tokens, use CanRead as authority, catch
the exception string as a retry signal, or add a new public policy framework.
The rule must cover a distinct same-route installation too, not just fallback.

Verify after fixing: the successor receives a fresh view, reads the expected bytes
under its own descriptor, and the predecessor remains unreadable. Retiring the
old view must not close the original anchor or the successor view.

### 3 Verify races and directly affected contracts

Run the new regression first, then the directly affected native picker/runtime
read cases, sparse worker handoff cases and bootstrap source failure cases.
Include these boundaries:

- Same-route queued work also receives a fresh installation-bound native view.
- Cancellation or supersession during native open cannot publish a stale view or
  terminalize a successor; held callbacks gain no new effects.
- Disposed or replaced native custody and cloned views still reject access.
- A genuine current source error still fails correctly.
- Ordinary seekable reuse and non-seekable source behavior are preserved.

Run at most one relevant broader contract lane after the fix stabilizes. Do not
rerun unrelated aggregate analyzer or smoke lanes merely for this new proof edge.
If deterministic reproduction needs more observability, add only the specific
diagnostic needed to distinguish stream binding or rejecting guard; do not widen
live trace limits as the first response.

### 4 Validate the targeted live route cycle

After review and focused green tests, checkpoint and rebuild the portable. Run one
targeted `regular-v4-live-activation-off-on-off-256mb` attempt outside the restricted
sandbox with strict preflights and the existing bounded trace. Do not bypass a
failed preflight or run the full matrix at this stage.

Acceptance remains unchanged: current fallback reads must progress, a second
coordinator-published `file_tuna_v4` route must be observed, and successful terminal
completion and in-run integrity must be proved. Regular and Tuna routes remain
protocol 4; post-Tuna fallback remains protocol 6; active Tuna V6 remains invalid.
SHA success or fallback completion alone cannot satisfy the route-cycle contract.

If the targeted run reaches a new first missing proof edge, retain that result and
prepare the next bounded RCA. Do not tune queue concurrency, chunk size, retry
cadence, goodput, wallet behavior, GUI setup or acceptance to mask this failure.

## Implementation and offline verification

The [new native queued-pump regression](<../tests/nLink.SmokeTests.Core/FrontierNativeQueuedPumpTests.cs>)
uses the original native picker factory without a stream or factory wrapper,
real native file custody, production Core services and coordinator transitions,
and the existing fake NKN transport. It performs no live NKN connection. Existing
send-completion and transport hooks hold a Tuna callback while the source hold
captures it, then retire the callback before releasing the fallback control
exchange. No route, runtime token or hold receipt is synthesized.

Before the fix, the [red regression](<../artifacts/native-queued-pump-20260930/native-queued-pump-red-joined.trx>)
failed. Its
[child diagnostic](<../artifacts/production-native-picker-20260929/pwsh-queued-pump-5e81830d9c3d4b009fb5306323766b27/frontier-process-output-0bf4e7e0ac4c4025b7cf07a1a0d96728-0-stderr.log>)
records `read_failed`, `native_sender_source_not_current`, `same_stream=True`,
`install=3` and `predecessor_readable=False`. The operational log records the
installation reason `queued_sparse_runtime_work`.

The runtime change is ten added lines across native source selection and pump
installation. Under the service gate, `IsReadBoundToPump` checks the original
selection, exact active stream reference and installation generation only to
choose reuse versus reopening. The existing factory and read guards retain all
custody, runtime, publication, installation and session checks. Ordinary source
factories take the unchanged path.

An initial always-reopen candidate was rejected by the startup regression:
startup already opens a view for its pending installation, and opening that same
generation again correctly fails. The final implementation retains that exact
view and reacquires only when a different view or installation is needed.
A test reflection lookup was also corrected to inspect the concrete native
Stream type rather than the Stream base class; no runtime guard was changed.

The [final focused regression](<../artifacts/native-queued-pump-20260930/native-queued-pump-green-same-route.trx>)
passes on the default test stack. It proves startup succeeds, held-callback
fallback installation 3 reads through a fresh native view, and normal pump
completion followed by queued same-route installation 4 also reacquires a view
without changing the runtime owner or route publication. Previous views remain
unreadable; cloned views cannot qualify for reuse. This is a bounded read/handoff
test, not a complete 256 MB transfer or second-Tuna acceptance proof.

The [native and joined compatibility run](<../artifacts/native-queued-pump-20260930/native-queued-pump-native-joined.trx>)
passed all 18 cases: nine native picker cases, including retired/disposed-source
rejection, and nine joined transport cases, including real-file 256 MiB completion
over the fake network. No live route-cycle acceptance is inferred from them.

The broader verification gate is not green:

- The [first affected run](<../artifacts/native-queued-pump-20260930/native-queued-pump-affected-contracts.trx>)
  recorded 60 passes and two callback-handoff assertion failures, then aborted
  with a stack overflow during a checkpoint handoff test's disposal.
- An [isolated installer baseline comparison](<../artifacts/native-queued-pump-20260930/native-queued-pump-baseline-failures.trx>)
  temporarily removed only the four new installer lines. That file matched HEAD;
  the unused native predicate and test additions remained. Both callback failures
  reproduced at the same assertions. The checkpoint test failed with
  `Coordinator snapshot envelope is not contiguous with the emitted stream tail`
  during disposal. Thus these three failures do not require the new reopen
  condition. Their underlying causes are not fixed by this change.
- The installer was restored and its SHA-256 checked against the previously
  tested content. The [remaining handoff cases](<../artifacts/native-queued-pump-20260930/native-queued-pump-remaining-handoff.trx>)
  recorded 50 passes and two further failures: a lost operational-log cursor in
  the regular route-cycle fixture and a wait cancellation in the Tuna switch-off
  recovery fixture. Previously completed methods were excluded from this run.

The isolated baseline, compatibility and remaining-handoff processes used
`DOTNET_DefaultStackSize=2000000` (hexadecimal, 32 MiB), the diagnostic setting
already documented for these Debug coordinator call chains. This is not a
production stack change or a default-stack gate pass. Build telemetry was also
disabled for focused rebuilds with `UsedAvaloniaProducts=` after Avalonia could
not write its optional log outside the workspace. No test assertion was disabled.

The [two isolated route fixtures](<../artifacts/native-queued-pump-20260930/native-queued-pump-isolated-routes.trx>)
were then rerun with fresh workspace-owned logging and the diagnostic stack.
Both still fail waiting for fallback publication: the regular cycle at
`SessionFileTransferV4SenderTests.cs:10457`, and the initial Tuna switch-off case
at line 31434. The lost-log-cursor error no longer masks the first wait.
Their [isolated log](<../artifacts/native-queued-pump-20260930/isolated-route-checks/nlink.log>)
is retained. These two cases have not been baseline-compared or root-caused;
do not describe them as fixed, harmless or caused by the native-stream change.

Across the final focused, affected, compatibility, baseline and remaining-case
reports there are 129 distinct passing cases and five distinct unresolved cases.
Expected pre-fix failures of the new regression are recorded separately, not
counted as unresolved failures of the final implementation. The aborted default
stack run and test-only stack qualification remain part of the result.

## Next verification boundary

Do not checkpoint for live validation or start another live run yet. The
[follow-up RCA](<file-transfer-verification-blockers-rca-fix-plan-20260930.md>)
resolves the callback and checkpoint cases and identifies the missing
correlated-bootstrap prerequisite in the two older route fixtures. Migrate
those fixtures and rerun their targeted paths without weakening currentness,
snapshot continuity or route acceptance.

The native-stream fix is retained uncommitted. Its focused regression, source
pins and wider verification results are recorded in the
[implementation review](<../artifacts/native-queued-pump-20260930/implementation-review.json>).
