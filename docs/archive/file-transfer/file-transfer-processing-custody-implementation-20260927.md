# Processing custody implementation — 2026-09-27

Status: bounded implementation and verification complete; Windows PowerShell
offline 256 MiB native writer association remains blocked by the unchanged
cumulative deadline. This is not a live-readiness claim.

Branch `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
The user's **Yes** approved the bounded proposal in
`file-transfer-processing-custody-contract-review-20260926.md`, including the
explicit change from ten shared observations to two inside one synchronous
processing check. The earlier review remains an immutable historical record.

## Implementation and limits

`Finalizer.OwnsFrontierBinding` selects a processing-only composition from the
original finalizer/binding/files/reader relationships and the original adopter's
active phase. It checks the shared native scope and terminal app closure before
and after the existing role-specific retained-file scan. Original identity,
finalizer request fields, cancellation, running clock and deadline are checked
again before return. Failure retires the original binding without retrying the
legacy traversal. Copied/foreign objects cannot retire originals.

`RetainedFilesCurrent` factors the existing native handle/type/identity/length
comparisons without changing their source-versus-payload semantics. The normal
adopter path still brackets this scan with its original `Current` calls.
`FinalSealAppsCurrent` factors the existing request/launch membership, ACK,
attachment completion, process exit/code and drained-output checks.

There is no cache, reusable validation token, new public authority API, timer
reset or altered five-second limit. The existing finalizer → owner → adopter
lock order remains; the composition does not acquire `sourceSync` or hold a
new lock across a source read. The source reader, streaming processor, parent
publication code, route acceptance and transport remain unchanged by this step.

The observation tradeoff is unchanged from the approved review: persistent
shared-scope changes between the two samples are detected; changes restored
between samples may escape observation. This is not an atomic cross-process
snapshot. A handle lost after its scan is detected by its next existing check.

## Verification record

Results are under `artifacts/processing-custody-20260927/results`.

| Execution | Result |
| --- | --- |
| `baseline-count.trx` | 2/2 before production edits: ten shared passes on both hosts. |
| `structure.trx` | 8/8: two processing passes, fresh second invocation, sixteen per original read; copy/foreign rejection and legacy ten-pass pre/post-processing behavior. |
| `faults.trx` | 98/98: opening/closing mapping, digest/body/tail/reserved/ACK/state, cancellation, signals, output loss, each retained handle, identity/length, clocks, phase changes and post-read rejection. |
| `final-controls.trx` | 10/10: direct original ledger-file loss, restored-byte sticky rejection, stopped-clock rejection and default-loader isolation. Four ledger tests supersede the earlier owner-disposal injection. |
| `compatibility.trx` | 105/105: selected buffered-reader, adoption, binding/source, terminal closure, native parent-death and parent facts/ACK/completion controls. The cursor-refill count is measured at 20 on both hosts. |
| `payload-writers.trx` | 2/4: both PowerShell 7 cases pass; both Windows PowerShell cases expire before native writer association. |

All new negative processing controls require that the intended injection ran,
that no legacy fallback occurred, and that every original retained/sealed file
handle closed before probing the retired reader again. The parent-signal hook
uses a controlled signaled wait handle; real native parent death is covered by
the existing separate compatibility fixture. Disposal is exercised from another
thread with a bounded wait. Parser EOF/output/capture/return and wrapper behavior
are covered by the unchanged buffered-reader controls.

There are **217 distinct passing focused/compatibility controls**, plus two
passing and two failing full-size cases: **219 pass / 2 fail** among 221 current
tests. The 227 executions also include the two historical baseline tests and
four repeated ledger controls. Compilation and targeted whitespace checks pass.

## Full-size results and remaining RCA

All four executions use the original **offline DevLocal** process tree and
268435456-byte payload, not a live NKN/GUI route-acceptance run. Artifacts are
under `artifacts/payload-writers-20260926` with these exact directory suffixes:

| Case / suffix | Original cumulative active work and outcome |
| --- | --- |
| `powershell-writer-valid-1b36d0e6fb2a4a5a985cbfd6b77b8d9e` | Copy 3060 ms; persist 3516 ms; prepare succeeds at 4942 ms; complete fails at 5001 ms. |
| `powershell-writer-foreign-c132cd4ab7a147b083318b32a42f5c28` | Copy 4197 ms; persist 4253 ms; prepare fails at 5002 ms. Facts attempted, ACK and completion not attempted; intended foreign-origin assertion not reached. |
| `pwsh-writer-valid-8a22dc7eab14463388735dc9ca4eccfa` | Copy 2289 ms; persist 2316 ms; prepare 2974 ms; complete succeeds at 3330 ms. Positive association and original cleanup pass. |
| `pwsh-writer-foreign-b248e2f84dc44c3aa208aa65d747cfed` | Copy 2320 ms; persist 2346 ms; prepare 3014 ms; complete succeeds at 3367 ms. The intended foreign-writer assertion is reached and passes. |

After all processes completed, independent exclusive opens of both the sent
and received payload files succeeded in **all four cases**, each with the
original full length. This verifies release, not successful association in the
failed cases. Failed artifacts are retained without reclassification or retry.

The positive Windows case advances beyond the previous preparation failure:
`TryPrepareParentHandoffWhenReady` returns true, whose existing sequence includes
ACK confirmation and the parent commit-prepared signal. Its subsequent
`TryCompleteParentHandoff` fails at the cumulative deadline. The foreign case
still has the earlier facts/ACK preparation gap. Therefore the earliest
unresolved proof edge across both Windows cases has **not** universally moved
to completion. Neither failure is a valid foreign-writer rejection.

The structural redundancy is removed as approved, but that does not prove the
whole Windows path fits five seconds. These coarse stage timestamps establish
remaining cumulative-budget exhaustion, not a particular inner failed predicate
or the cause of the copy-time variation. No new runtime heuristic is justified
by this evidence.

Next bounded RCA/fix plan:

1. Add rejection-only, test-opt-in phase/call-cost diagnostics to the original
   post-processing/facts/ACK path, preserving every original check and clock.
   Start with the Windows foreign case, the earliest remaining proof gap;
   distinguish processor, semantic copy, persistence and parent-work costs.
2. Identify the first failed inner predicate and the work charged before it.
   Do not infer it from an attempted flag, or rerun until timing happens to pass.
3. Propose a surgical equivalent optimization if the evidence supports one.
   Any change in observation granularity outside processing requires a separate
   contract review/approval; this approval does not extend to parent validation.
4. Verify only the changed contract and targeted Windows cases, retaining the
   five-second limits, genuine foreign-origin assertion and cleanup checks.

## Preservation and next gate

All 322 prior review-checkpoint pins verified before implementation. Current
comparison changes only the eight intended existing source/support paths.
The baseline had 307 dirty files: four are intentionally updated, 303 retain
their original bytes. Four previously clean production files and two new test
files are also in this change; this report is new.

The next gate is the complete original-process offline 256 MiB native
payload/writer association on both hosts within unchanged clocks. A foreign
control that expires before its intended assertion is a failure, not a pass.
After that, wrapper-to-original-parent payload adoption, primary/analyzer
consumption and live validation remain later milestones. No full GUI matrix,
live NKN test, portable rebuild, commit, push or acceptance relaxation is part
of this implementation. Live route-cycle acceptance still requires a second
coordinator-published `file_tuna_v4` route.

The recovery seal is
`artifacts/processing-custody-20260927/FINAL-VERIFICATION.json`. It records the
source inventory, exact test outcomes, current full-size artifacts, prior seal,
unchanged guards and remaining proof gap. Nothing is staged or committed.
