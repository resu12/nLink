# Original writer-join invalidation RCA — 2026-09-27

Status: **the latest natural rejection coincides with the original GUI work
clock crossing its strict 10,000 ms limit at 10,001 ms**. The join retires while
validating the helpee's closed-writer snapshot. Clone disposal and repeat binding
pass earlier in this run. The immediate budget blocker is established; the
individual operation responsible for the accumulated cost is not yet measured.

Eight focused diagnostic controls pass. One fresh offline canonical attempt
completed its 256 MiB transfer but failed before payload offering. It remains an
integration failure, not acceptance. No functional runtime fix or limit change
was made.

Branch `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-payload-offer-rca-20260927.md`.

## Diagnostic implementation

Only three existing explicit-profile test/support files changed:

- `FrontierAttemptWriters.cs` preserves the seven original short-circuit
  dependency checks, in order, with one evaluation each. On rejection it records
  the reached stage, existing GUI `disposed` field, original `frontierWork`
  elapsed/running state and UTC. Logging is best-effort and does not call an
  ownership predicate, start/stop/reset a clock, or change the returned result.
- `FrontierAttemptWritersJoin.ps1` splits the original copy/repeat assertion into
  its already-evaluated currentness result and conditional repeat result. The
  repeat still runs only when the original currentness check succeeds. The
  original exception remains unchanged for every existing scenario.
- `FrontierAttemptWritersTests.cs` adds an exhausted-clock control and diagnostic
  assertions. That new control waits 10,050 ms while charging the actual original
  work clock before the existing copy/repeat guard, and requires rejection with
  no repeat evaluation. It neither resets nor substitutes a work clock.

All additions remain in the existing `NLINK_TEST_ATTEMPT_DISPATCH` profile or its
test scripts. Normal-loader runtime code, authority/custody predicates, byte and
work limits, route acceptance, transfer configuration and listener timeout are
unchanged. The new negative case is not selected by the canonical scenario.

## Focused verification

`artifacts/role-join-rca-20260927/results/role-join-diagnostics.trx`:
**8 passed, 0 failed, 0 skipped**, run once.

Both PowerShell hosts cover:

1. The original positive, including copy disposal, repeat refusal and subsequent
   deliberate manifest replacement. Its intentional replacement is diagnosed at
   `helpee_manifest`, not mistaken for a natural failure.
2. Original parent cancellation: `stage=attempt`, `gui_disposed=False`, with only
   200 ms / 275 ms accumulated. This is distinct from the budget control.
3. Exhausted original clock: `stage=attempt`, `gui_disposed=True`, 10,255 ms /
   10,336 ms, `original_current=False`, `repeat_evaluated=False`, and no returned
   repeat join. Existing terminal no-revival checks remain in force.
4. Default-loader exclusion of the test-only role-join capability.

Exact control diagnostics are in `control-observations.json` under the new
artifact root. PowerShell parsing and changed-file whitespace checks pass.
No aggregate suite or unrelated contract lane was rerun.

## One fresh canonical attempt

Directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-54fa1f79872d4f3e9b785f77351ea5ae`.

Result:
`artifacts/role-join-rca-20260927/results/canonical-role-join-diagnostic.trx`:
**1 failed, 0 passed**, preserved. This was the sole canonical attempt this step.

The decisive original GUI diagnostic is:

```text
role-writer-inputs-rejected; stage=helpee_closed_writer; gui_disposed=True; active_ms=10001; work_running=False; utc=2026-09-27T12:05:05.1166070Z
```

The resulting assertion is **Source/destination writer custody did not coexist**
in `FrontierAttemptDestinationJoin.ps1:8`. The earlier original copy/repeat guard
and both source/destination writer-binding sequences passed. There is no
`role-writer-copy-repeat-failed` diagnostic in this run. The coexistence assertion
does not prove which of its three outer predicates failed; the inner diagnostic
does identify the rejected role dependency and its original clock.

The first missing edge is therefore current original writer custody during the
source/destination coexistence check, before payload offering. The subsequent
payload normal-exit and parent-handoff errors are downstream. No payload-offer
diagnostic, native writer-handoff success marker or original primary analysis
was produced.

### Why this is a budget blocker, not a clone/repeat fix

`MatchesClosedWriter` validates the original immutable snapshot's currentness
before accepting its closure facts. Snapshot currentness traverses its original
capture/source ownership. The original GUI/adopter checks enforce the same
cumulative clock: `LocalGuiWriterLedgerOrigin.cs` rejects elapsed work above
`MaximumActiveMilliseconds`, and `LocalFrontierFileAdoption.cs` defines that
limit as 10,000 ms and also checks it around native validation. Rejection retires
the GUI/adopter and subsequently the role join; disposal stops the existing
clock, explaining `work_running=False` at the failure observation.

At 10,001 ms, current custody cannot be accepted under this unchanged contract.
No clock was consumed artificially in the canonical run. The new spent-budget
scenario is a separate small writer-only control, not the selected canonical
case. The budget condition alone is sufficient to require rejection; this
diagnostic is not an exhaustive exclusion of every concurrent failure.

The previous run stopped at the earlier copy/repeat guard without clock data.
The new failure moving to a later currentness check is consistent with a
cumulative-cost boundary. It does not retrospectively prove the exact cause of
every historical intermittent role-join failure. No clone-disposal or repeat-
binding code should be changed based on these runs.

Crucially, the reached predicate is not necessarily the expensive operation.
This clock includes earlier source sealing, ledger processing, original matches
and subsequent nested custody checks. No stage timings or call-count profile
were retained in this run. Neither the 101k-line processing pass nor the fixture's
negative probes can yet be assigned the dominant cost. Do not remove checks,
cache authority or increase the budget based only on where it expired.

### Independent listener expiry remains

The wrapper also records:

```text
payload-wrapper-adoption-failed; stage=connect; exception_type=OperationCanceledException; elapsed_ms=100008; lifetime_cancelled=False; work_ms=0; work_running=False; utc=2026-09-27T12:04:52.2801011Z
```

This is its separate 100,000 ms pre-offer timer, not the 10,000 ms GUI active-work
clock or 5,000 ms wrapper active-work clock. The GUI ran from 12:03:11.9515825Z to
12:05:05.2057358Z (113.254 seconds), inside its 210-second scenario deadline;
`timedOut=false`, exit code 1. Fixing only the GUI work blocker will not restore
a listener that has already expired. Keep these as separate sequential fixes,
not a shared timeout increase.

## Transfer and preservation evidence

Both original apps report **268,435,456 bytes**, `post_tuna_fallback_v6`, matching
session/transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent hashes of the source and retained destination match. GUI destination
adoption and source-delivery markers are present. Both files permit exclusive
access after cleanup, and GUI 21500 / sender 56688 / receiver 29464 have exited.

The original source has **101,924 lines / 92,132,805 bytes**, SHA-256
`ba43d9b3948d85d576b9dfcd798e83dd2607f7f569f6160c3825e941072bccca`.
At physical lines 1001 / 1008, fallback receiver state 2 applies from previous
epoch -1 with credit 512, then its repeat remains duplicate and unapplied.
Retained/writer/semantic outputs contain 101,922 / 101,924 / 73,771 lines, totaling
277,617 references. Counts and files are not substitutes for native handoff.

Exact observations/hashes are in
`artifacts/role-join-rca-20260927/canonical-observation.json`.

## Bounded fix plan and next step

1. **Attribute the existing cumulative cost without another authority
   evaluation.** Record original clock deltas around the already-executed seal,
   observed-ledger processing, test-only role probes, source binding, destination
   binding and coexistence checks. Distinguish test-only negative-probe overhead
   from the genuine handoff path. Profile only existing calls; do not use the
   older helper that repeats ownership checks 32 times. Clock samples are
   diagnostic, never permission to reuse an earlier successful check.
2. **Optimize only the measured operation under the current contract.** Prefer
   cheaper exact computation/native comparison retaining every required byte,
   identity, owner and cancellation check at the same boundaries. Do not remove
   negative probes from the canonical path, reduce validation frequency, cache
   a custody verdict, create a new work allowance, or move work outside its
   original clock without a separately reviewed contract. If exact equivalent
   work cannot fit, present the explicit tradeoff instead of silently changing
   limits. No throughput/queue/chunk/retry tuning is indicated.
3. **Verify only the affected guards**, including original cancellation, cloned
   inputs, exhausted original work, no revival and native-file closure. Run one
   targeted offline canonical attempt after a justified implementation; stop at
   and retain any newly exposed first edge. Do not rerun the GUI matrix.
4. **Then implement the separately reviewed listener-lifetime correction** from
   the prior report, with delayed-valid-offer and owner-cancellation/exit cleanup
   controls. Preserve the 10-second connected exchange and 5-second wrapper work
   limit. This remains a later step, not part of this diagnostic checkpoint.

The immediate next step is non-authoritative cost attribution, followed by a
bounded optimization only if its equivalence is demonstrated. No functional fix
has been guessed from the final failed predicate. Primary evaluation and the
second coordinator-published `file_tuna_v4` remain required; SHA/fallback success
does not satisfy the route-cycle contract. No live test is due yet.

All 2,401 preceding pins matched before this step. Of the 392 existing dirty
entries, only the three test/support files listed above were edited. Previous
reports/artifacts remain unchanged. Recovery is sealed in
`artifacts/role-join-rca-20260927/FINAL-VERIFICATION.json`.
No portable rebuild, live network run, full GUI matrix, commit or push occurred.
