# Parent take first-refusal diagnostics

Date: 2026-09-27. Status: ten focused checks pass; the single rebuilt offline canonical attempt identifies `native-parent-match` as the first refusal.

## Scope and preservation

Preflight verified all 5,005 pins in
`artifacts/parent-fixture-layout-20260927/FINAL-VERIFICATION.json`, SHA-256
`b37f98b538bb15651774b0b6a54de1478183fd536c0c51fe0d9b34f7785a551a`.
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`;
there were 410 existing dirty entries. Existing edits and artifacts are retained.

Only explicit offline-test-profile code and its tests changed. No runtime
authority, ownership contract, acceptance rule, transport behavior, timer or
work allowance is changed. No live GUI/NKN run, full matrix, commit or push.

## Implementation

`AttemptPayloadParentForTest.TryTakeAfterExitForTest` now labels each of its
27 existing rejection predicates in the same short-circuit order. Rejection
records a string and returns null. The diagnostic getter is an auto-property:
it does not reevaluate currentness, custody, files, channels or clocks. The
original locks, one-shot flag, native operations, work start/stop and cleanup
remain in place. Successful completion records `accepted`.

There is no diagnostic I/O inside the native take or its work-clock interval.
The PowerShell fixture prints the stored first-take result after the original
call returns. It performs and prints the existing repeated-take check only if
the initial result matches the expected outcome, preserving the original
short-circuit behavior and assertion failure text. Copied-instance and
copied-report controls verify distinct refusal labels without taking custody.

## Focused verification

Results directory: `artifacts/parent-take-diagnostics-20260927/results`.

- The source-contract test passes: all 27 predicates and labels retain their
  order; native relay take, custody take and native-parent match each occur
  once, file checks three times, channel-current checks twice, and work
  start/stop once. Diagnostic storage performs no authority evaluation.
- Both original positive parent handoffs pass. The first take reports
  `accepted`; the repeat reports `already-attempted`.
- Cancellation reports `disposed-before-take`; foreign-writer association
  reports `native-parent-match`; exhausted parent work reports
  `work-before-native`. All three reject the first take and remain retired
  on the repeat. Their genuine native-file cleanup assertions pass.
- Default-loader isolation passes on both PowerShell hosts.

The first control run had seven passes and two failures in newly added C#
assertions, not in the handoff implementation. The assertions incorrectly
required take diagnostics for `wrapper-nonzero` and `wrapper-killed`. The
existing outer workflow deliberately skips take when wrapper exit is nonzero
(`Run-FileTransferRouteAcceptance.ps1`, exit-code gate before relay adoption).
Both fixtures had already completed their expected failure and cleanup checks.
The assertions were corrected to require no take diagnostic for these two
cases. Both corrected cases pass in the targeted rerun; other completed
controls were not repeated. The combined gate has ten distinct passing checks:
one source-contract, seven genuine parent fixture cases, and two default-loader
isolation checks. No broader or unrelated aggregate suite was run.

## Single rebuilt canonical attempt

The smoke assembly rebuilt with zero warnings/errors, followed by exactly one
offline `CanonicalFallback_RequiresOriginalHandoffsBeyondOldOriginCap` attempt.
It failed after approximately 115 seconds. Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-f9280950d85b4bd288593e83b374d918`.

The decisive retained diagnostic is:

```text
parent-payload-take; call=first; result=False; reason=native-parent-match
```

There is no repeat-call diagnostic: the original first-result assertion failed
and short-circuited the repeat, as required. The failure is not a repeated-take
acceptance. `AnalysisBoundary` and the corrected filesystem probe were not
reached, and no primary worker copy, stdout or receipt exists.

The label is emitted only after the original wrapper exit/output checks,
committed payload-channel and file checks, stopped-work check and pre-native
5,000 ms check pass, and both the native relay result and custody object are
non-null. The failed predicate is the original
`native.MatchesParentPayloadForTest(...)`. This does not mean the native
custody object was successfully accepted or remains current.

Read-only inspection shows that method short-circuits these checks:

1. Exact original custody object and owner request.
2. Reference identity of the accepted relayed completion.
3. Native custody currentness.
4. GUI request identity and completion hash.
5. Completion facts and exact payload-writer facts.

The latest artifact does not preserve which of those inner predicates refused.
In particular, `TakeRelayAfterExit` can return a non-null relayed completion
even when its native `Accept` fails and custody is retired. Therefore a non-null
result/custody is not evidence that native acceptance succeeded. The
foreign-writer negative control reaches the same outer label, but does not
prove this valid canonical attempt has a foreign writer. No timer, identity,
metadata, lifetime or ownership root cause is asserted from this aggregate
inner check.

## Completed work and cleanup evidence

GUI PID 43956 exits 0, output capture completes, no timeout occurs, and no
deadline extension is requested or adopted. GUI cumulative active work at
cleanup is 6,489.9197 ms, below its unchanged 10,000 ms allowance. GUI source,
destination and native-writer handoffs, wrapper payload verification and parent
payload offer markers are present.

Native PIDs 19740 and 16916 each report 268,435,456 bytes on
`post_tuna_fallback_v6`, with matching session, transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent post-run checks confirm both payload lengths/hashes, exclusive
read/write access after cleanup, and all three PIDs no longer running.

The finalizer retains all 101,856 source lines, with 101,854 retained, 101,856
writer and 73,709 semantic lines. These diagnostics and file checks establish
progress/cleanup only, not live acceptance or the required second
coordinator-published `file_tuna_v4` route.

## Next bounded step

Preserve the first failing predicate inside `MatchesParentPayloadForTest` using
the existing evaluations in their original order. Do not re-call `IsCurrent`,
native `Accept`, `SameCompletion` or either facts matcher for diagnostics.
Keep this in the explicit test profile; do not change production relay logic.
Verify genuine valid/foreign-writer controls plus guard ordering, then rebuild
for one offline canonical attempt. Only after that evidence identifies the
actual missing association edge should a behavioral fix be designed.

No retry, production fix, timer/acceptance change, live GUI/NKN run, portable
rebuild, full matrix, commit or push occurred in this step. The recovery seal is
`artifacts/parent-take-diagnostics-20260927/FINAL-VERIFICATION.json`.
