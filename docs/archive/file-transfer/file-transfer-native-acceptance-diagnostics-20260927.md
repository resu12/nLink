# Original native relay acceptance diagnostics

Date: 2026-09-27. Status: nine focused checks pass; the single rebuilt offline
canonical attempt finds native custody already closed before its first take.

## Scope and implementation

Preflight verified all 5,361 pins in
`artifacts/parent-association-diagnostics-20260927/FINAL-VERIFICATION.json`,
SHA-256 `e202484e288cc8c38a2992cee4f1fdf2a59494fad6eb49344d2eea3ab294b8a4`.
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`;
there were 412 existing dirty entries. Existing work and evidence are retained.

The native relay source now has diagnostic branches under the existing
`NLINK_TEST_ATTEMPT_DISPATCH` symbol. The seven original `BeginTake` and nine
original `Accept` guard predicates remain in their original short-circuit
order, with their original boolean values. Scope-byte rejection and caught
exceptions receive labels too. Begin exceptions still propagate; acceptance
exceptions still return false. No guard, currentness, facts, body or scope
comparison is evaluated twice.

The first native `Close` captures only existing diagnostic strings and the
raw `taking`/`accepted` state, after the original closed guard. Later closes
cannot overwrite it. This snapshot identifies the phase, not the close caller.
The test parent copies those strings immediately after the original relay take;
the existing first-take line prints them after the call returns. There is no
new native diagnostic I/O, polling, timer, authority evaluation or retry.

Normal/default source, projected without the test symbol and normalized to LF,
retains its pre-edit SHA-256:
`983bf3a6138949c6da89e0c459d17d10cee2bd095ea612092c171937e12bdd78`.
The default loader exposes neither the diagnostic fields nor their helper in
Windows PowerShell or PowerShell 7. The production source file is edited, but
its default-build implementation is unchanged.

## Verification

Results: `artifacts/native-acceptance-diagnostics-20260927/results`.

| Focused gate | Result |
| --- | --- |
| Default-source projection and native predicate-order controls | 2 passed |
| Parent diagnostic-copy/guard-order control | 1 passed |
| Genuine valid native handoffs, Windows PowerShell and PowerShell 7 | 2 passed |
| Native scope-byte mismatch and already-disposed custody controls | 2 passed |
| Default-loader isolation, both hosts | 2 passed |
| Smoke assembly rebuild before canonical attempt | Zero warnings/errors |

The first combined source-test run had two passes and one failure: the existing
parent guard extractor incorrectly included the new non-returning diagnostic
copy block in the next guard. Its regex now excludes statement/block separators.
Only that affected check was rerun, and passed. This was a test matcher issue,
not a native handoff failure. The retained initial TRX is not presented as green.

Both real valid handoffs report `native_begin=ready; native_accept=accepted;
native_close=open`, accept once, and reject the repeated take. The scope control
changes byte zero of the original native scope copy; it reports
`native_accept=scope-byte:0` and records first retirement after begin. The
disposed control explicitly disposes the original custody before take; it
reports both native guards as `closed` and first retirement before either call.
Both negative controls still reject the repeated take and release payloads.

No unrelated cancellation, budget, coordinator, analyzer or aggregate suite was
rerun. The stable focused gate contains nine distinct passing checks.

## Single rebuilt canonical attempt

Exactly one offline `CanonicalFallback_RequiresOriginalHandoffsBeyondOldOriginCap`
attempt followed the rebuild. It failed after approximately 117 seconds.
Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-06c9dfe10ab04d7a9d52e4f44fd4470e`.

The retained original first take is:

```text
parent-payload-take; call=first; result=False; reason=native-parent-match; association=accepted-result; native_begin=closed; native_accept=closed; native_close=begin:not-called,accept:not-called,taking:False,accepted:False
```

This is the `valid` parent case, not either newly injected negative control.
The earlier native close occurred before `BeginTake` or `Accept` ran, while
`taking` was false and no completion was accepted. Both calls subsequently
stopped at their first `closed` predicate. Their later wrapper, transfer, pins,
completion-facts and scope checks were not reached. The association's
`accepted-result` mismatch is therefore downstream of already-retired native
custody, not evidence of a completion-facts or scope mismatch in this run.

The original outer outcome assertion remains failed. It skips the repeated
take; `AnalysisBoundary`, its file-protection probe and primary analysis are
not reached. No copied primary worker, primary stdout or primary receipt exists.
There was no second canonical attempt and no acceptance relaxation.

## RCA boundary and next plan

The new first missing edge is continuity of the original native relay custody
from pull to the first post-exit take. The snapshot proves retirement before
take, but does not identify the first close caller or its triggering condition.
It does not prove a timer expiry, bad wrapper exit, transfer cancellation,
forwarding failure or incorrect cleanup.

Read-only inspection identifies the existing retirement paths to distinguish:
transfer-cancel callback, owner-close callback, wrapper-exit callback,
unsuccessful native pull cleanup, explicit native disposal, and original
request/registration disposal. The post-take failure cleanup cannot explain
this first-close snapshot. Native `IsCurrent` returns false without closing
when no completion is accepted; begin/accept had not run at the first close.

Next bounded diagnostic/fix-plan step:

1. Record the first actual native retirement entry point and the original
   triggering predicate/exception under the existing lock. Cover the possible
   pre-take close paths together; capture already-evaluated results only.
   Do not re-run currentness, channel-state or exit checks for reporting.
2. If a transfer/watch path triggers retirement, retain its original pull or
   forward refusal context in the same bounded trace. Keep a clear distinction
   between cancellation observed locally and the upstream cause of cancellation.
3. Preserve close idempotence, default-build source, lock order, callbacks,
   cleanup, budgets, original terminal decisions and authority. Verify focused
   successful/negative retirement controls, then rebuild for one offline
   canonical attempt. Fix only the proven retirement edge after its cause is
   known; do not suppress legitimate cancellation or accept closed custody.

## Completion and cleanup

GUI PID 49836 exits zero with complete output capture, no timeout and no deadline
extension. GUI cumulative active work is 6,548.5228 ms of the unchanged
10,000 ms allowance. Original source/destination adoption, native-writer handoff,
wrapper verification and parent-offer markers are present.

Native PIDs 57020 and 5212 each report 268,435,456 bytes on
`post_tuna_fallback_v6`, with matching session, transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent post-run checks confirm both payload lengths/hashes, exclusive
read/write access to both files, and all three processes no longer running.

The source retains 101,853 lines; retained/writer/semantic counts are
101,851 / 101,853 / 73,708. These are offline progress and cleanup facts, not
live acceptance. A second coordinator-published `file_tuna_v4` remains required
for the live route-cycle contract.

No live GUI/NKN run, portable rebuild, full matrix, behavioral fix, timer change,
acceptance change, commit or push occurred. Recovery checkpoint:
`artifacts/native-acceptance-diagnostics-20260927/FINAL-VERIFICATION.json`.
