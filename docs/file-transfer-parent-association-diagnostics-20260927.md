# Parent association first-refusal diagnostics

Date: 2026-09-27. Status: five focused checks pass; the single rebuilt offline canonical attempt identifies `accepted-result` as the first association refusal.

## Scope and implementation

Preflight verified all 5,255 pins in
`artifacts/parent-take-diagnostics-20260927/FINAL-VERIFICATION.json`, SHA-256
`7198d812fcc940cc0f9bcb37f45301601dc84f2835fa390a26f1a4a65fdf3458`.
HEAD is `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`;
there were 411 existing dirty entries. Existing work and evidence are retained.

Only the explicit offline test profile and its tests change. The original eight
predicates in `MatchesParentPayloadForTest` retain their short-circuit order:
original custody, owner, accepted completion, currentness, GUI request,
completion hash, completion facts, and payload-writer facts. Each refusal
stores a label and returns false; success stores `matched`.

The original caller invokes the matcher once, copies its stored diagnostic,
then branches on that same boolean result. Its diagnostic starts as
`not-evaluated` on each take, so an earlier refusal cannot report a previous
association result. The existing PowerShell first-take line includes the stored
association label after the call returns. No diagnostic I/O is added inside the
native matcher or active-work interval.

No currentness, hash or facts evaluation is repeated. Native `Accept`, relay
take, custody take, one-shot behavior, locks, cleanup, clocks, authority flags
and route acceptance remain unchanged. Production relay logic is not edited.

## Verification

Results: `artifacts/parent-association-diagnostics-20260927/results`.

| Gate | Result |
| --- | --- |
| New association source-contract control before implementation | Expected failure |
| Association and caller guard-order/single-evaluation controls | 2 passed |
| Genuine valid handoffs, Windows PowerShell and PowerShell 7 | 2 passed |
| Genuine foreign-writer rejection | 1 passed |
| Smoke assembly rebuild | Zero warnings/errors |

Both valid handoffs report `association=matched`, accept the original first
take, and reject the repeat as `already-attempted`. The foreign-writer control
reports `association=payload-writer-facts`, rejects the first take, and remains
retired on the repeat. Original native-file cleanup checks pass in all three.

The combined current gate is five distinct passing checks. No unrelated
cancellation, budget, default-loader, wrapper or aggregate lane was rerun.

## Single rebuilt canonical attempt

Exactly one offline `CanonicalFallback_RequiresOriginalHandoffsBeyondOldOriginCap`
attempt followed the rebuild. It failed after approximately 113 seconds.
Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-0f7c48df557d482697513db24019e111`.

The original first take retains:

```text
parent-payload-take; call=first; result=False; reason=native-parent-match; association=accepted-result
```

The exact failed predicate is
`Object.ReferenceEquals(accepted, result)` inside the original native custody.
The original-custody and owner-request identity checks passed first. The
association's `IsCurrent`, GUI request, completion hash, completion facts and
payload-writer facts checks were not evaluated after that refusal. This is not
the foreign-writer negative control's `payload-writer-facts` failure.

The existing outcome assertion still fails and skips its repeated-take call.
`AnalysisBoundary`, the filesystem protection probe and primary analysis are
not reached. No copied primary worker, primary stdout or receipt exists. No
second attempt or acceptance relaxation was made.

## RCA boundary and next diagnostic plan

Read-only source review confirms that `FrontierRelayCustody.Accept` records
`accepted = result` only on its successful path. `TakeRelayAfterExit` calls
`BeginTake`, decodes the returned completion, and calls native `Accept`; it can
still return that completion when native acceptance fails. Its finally block
then retires unaccepted custody. Therefore the missing proof edge is native
acceptance of this exact returned completion, not its mere existence.

The latest artifact proves the reference mismatch, but does not retain whether
`accepted` was null or another object, which `BeginTake`/`Accept` predicate
refused, or which earlier retirement caused that refusal. Do not infer a race,
timeout, mutated completion, copied owner or mismatched writer from this label.
No previously successful association check should be bypassed.

Next bounded step:

1. Preserve the first original `BeginTake`/native `Accept` refusal or caught
   exception through test-profile-only diagnostics. Record retirement context
   only from existing execution, without adding new authority evaluations.
2. Keep all production return values, short-circuit order, locks, one-shot
   state, native ownership, cleanup and timer/work budgets unchanged. Do not
   invoke `Accept`, currentness or facts checks a second time for reporting.
3. Verify focused native acceptance/rejection and diagnostic ordering controls,
   including that the default loader has no new diagnostic surface; rebuild
   for one offline canonical attempt. Design a behavioral fix only after the
   original native acceptance failure is identified.

This requires diagnostic visibility at the earlier native relay boundary; no
such production-file instrumentation was added in this step.

## Completion and cleanup evidence

GUI PID 53260 exits 0 with complete output capture, no timeout, and no deadline
extension. GUI cumulative active work reaches 6,767.9458 ms of the unchanged
10,000 ms allowance. Original GUI source, destination, native-writer handoff,
wrapper verification and parent payload-offer markers are present.

Native PIDs 46964 and 51116 each report 268,435,456 bytes on
`post_tuna_fallback_v6`, with matching session, transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Post-run checks independently confirm both file lengths/hashes, exclusive
read/write access to both payloads, and all three processes no longer running.

The source retains 101,846 lines; retained/writer/semantic counts are
101,844 / 101,846 / 73,700. These are progress and cleanup facts, not live
acceptance. The second coordinator-published `file_tuna_v4` route remains
required for the route-cycle contract.

No live GUI/NKN run, portable rebuild, full matrix, behavioral fix, commit or
push occurred. Recovery seal:
`artifacts/parent-association-diagnostics-20260927/FINAL-VERIFICATION.json`.
