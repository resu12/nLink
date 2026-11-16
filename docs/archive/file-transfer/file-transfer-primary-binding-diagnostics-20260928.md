# Primary payload binding: first-error diagnostics and RCA

Date: 2026-09-28. Status: diagnostic fix verified by 17 focused/native checks.
One rebuilt offline canonical run identifies the first failure as a stale
fixture expectation, not a demonstrated handle leak. Production code, binding
expectations, work budgets, and route-acceptance requirements are unchanged.

## Diagnostic fix

The fixture previously threw one cleanup error for three different conditions:
an incomplete Bind-stage flag, open payload handles, or a current evaluation.
Because that guard runs inside the analyzer's finally path, it could replace
an earlier Bind exception.

The test-only projection now saves and rethrows the original Bind error.
Original custody disposal still runs in its original location. A subsequent
cleanup error is recorded but cannot replace that already-unwinding Bind error;
without a Bind error, cleanup errors still fail normally. The projection helper
is shared with focused controls so they exercise the actual injected guards.

The cleanup checks preserve the original short-circuit order and evaluate each
original observation at most once. Skipped handle/currentness checks are
reported as `not_checked`, not false or zero. The binding diagnostic records
the non-null result of the original call and its existing expected outcome;
it performs no extra binding or authority evaluation.

## Verification

- Twelve focused controls pass on Windows PowerShell and PowerShell 7: an
  interrupted Bind, an interrupted Bind with disposal failure, successful
  cleanup, open handles, a current evaluation, and no evaluation. They verify
  original error/stack preservation, exactly one disposal attempt, unchanged
  short-circuit observation counts, and rejection of cleanup failures.
- Five existing full-size native regressions pass: positives on both hosts,
  foreign completion, retired pair, and retired native owner. They retain the
  original three evaluations/one native read, independent duplicate retirement,
  and the original incomplete-proof rejection. Both lanes pass on their first
  run (7 seconds and 5 minutes 5 seconds respectively).
- Four affected PowerShell scripts parse successfully. No production source
  changed. A full non-incremental build succeeds in 19.63 seconds with zero
  errors and 357 warnings. The warnings are retained in `smoke-build.log`;
  none names the newly added diagnostic fixtures. No warning cleanup is claimed.

Results and build log: `artifacts/primary-binding-diagnostics-20260928/`.

## Canonical evidence and root cause

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-8ddc70d5e83843e0b6b3c3daabe7f68e`.

The original native call reports:

```text
attempt-payload-primary-binding; case=canonical-fallback; expected=False; actual=True
attempt-payload-primary-first-error; stage=bind; reason=Actual transfer evaluation association outcome changed
attempt-payload-primary-cleanup; bind_complete=False; open_handles=not_checked; evaluation_current=not_checked
```

The first throw is now correctly attributed to
`FrontierAttemptPayloadPrimaryProbe.ps1:41`. Its expectation table at line 3
includes only `valid` and `canonical-source`, so it demands rejection for
`canonical-fallback`. The unchanged native method instead accepts the original
pair, arrays, context, views, and current native files. Copied/foreign probes
precede this call and did not trigger their rejection assertion.

This is a fixture classification error. The reviewed
`file-transfer-payload-primary-handoff-20260927.md` contract explicitly defines
this association as evidence custody, separate from live proof, transfer
integrity, and publication authority. Semantic route success is not a binding
precondition. `canonical-fallback` legitimately reaching that association must
not be treated as an invalid owner solely because route proof remains incomplete.

The expectation mismatch throws before the fixture sets its Bind-stage-complete
flag. That explains the secondary cleanup condition. Here `bind_complete=False`
means the fixture's assertions did not finish; it does not contradict the
recorded successful native binding. This run does not claim an independent
in-process handle-retirement pass, because those checks were short-circuited.

All 73,644 source-event origins remain positive and strictly increasing in
1,151 batches (44 entries in the last batch). Genuine EOF contains 69,660,164
code units. Original cumulative work is:

| Component | Seconds |
| --- | ---: |
| Attach | 0.0347620 |
| Open | 0.0009775 |
| Read | 17.7668539 |
| All origin batches | 1.1060654 |
| Total after origins | 18.9086588 |

These components reconcile exactly. Final work is 18.9383126 seconds, below the
unchanged 20-second limit. Resource/base rejection and source exception remain
`none`; first close is `Dispose` with true EOF. No scalar origin calls replace
the batches. The facts body is 1,926,891 bytes without an acknowledgment channel.

The integration still fails: two evaluations, one native
parser invocation, zero diagnostic receipts, no complete proof artifacts, and
no parity result. The saved original failure survives the cleanup path. The
outer source-count and incomplete-proof assertions remain strict and also fail;
they were not relaxed to turn this diagnostic run into a pass.

## Narrow next fix plan

1. Classify only `canonical-fallback` as a valid original payload-pair binding
   in the fixture's expectation table, matching the existing custody contract.
2. Apply the existing positive assertions to that case: current original pair,
   256 MiB count/hash, copied/repeated rejection, false authority flags, and
   independent retirement. Keep foreign/retired negative cases unchanged.
3. Rebuild and rerun the targeted offline canonical once. Require the existing
   all-origin vector, three evaluations/one native read, and original proof/
   parent rejection rules. If a later assertion fails, report its first edge
   rather than adjusting further expectations speculatively.

That expectation fix is not implemented in this diagnostic step. No runtime,
receipt catalog, witness selection, route acceptance, or budget change is needed
to expose the now-confirmed mismatch. Live testing is not the next step.

## Cleanup and preservation

Primary PID 42692 exits 1 without timeout. Offline GUI PID 46204 exits 0 with
complete capture and no deadline extension, using 6,014.2804 ms active work.
Native PIDs 27460 and 37476 report matching 268,435,456-byte payloads on
`post_tuna_fallback_v6`. Both files independently match SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`;
exclusive read/write access succeeds, and all four processes are gone.
This verifies post-run release, not the skipped in-process cleanup assertion.

Source/retained/writer/semantic counts are 101,782 / 101,780 / 101,782 / 73,644.
The unchanged before/after analyzer input hash is
`2b067b5e2835406cc9bbe3d41980c22e7605a6ec8fcd62dc94a78b3a3bc4ea95`.
Exactly one rebuilt offline canonical was run (3 minutes 22 seconds). There was
no live GUI/NKN run, portable build, full matrix, commit, or push. A second
coordinator-published `file_tuna_v4` remains mandatory for live route-cycle
acceptance; payload integrity and native custody do not substitute for it.

The starting seal was
`artifacts/primary-origin-batch-20260927/FINAL-VERIFICATION.json`, SHA-256
`95330f5594a8e22473d17f00c67eaf2ed519bf21fc8a9a89fb6d17873d8c44ee`.
All 8,009 pins matched before edits. The 434 starting dirty entries are preserved:
only two fixture scripts changed; three fixture/test files and this report were
added. No starting paths were removed or changed status (438 current entries).
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.
The final seal and hash audit are in this step's `FINAL-VERIFICATION.json` and
`FINAL-CHECK.json` artifacts.
