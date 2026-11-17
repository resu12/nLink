# Primary binding classification and read-budget checkpoint

Date: 2026-09-28. Status: the approved one-line fixture fix is implemented.
Both focused classification tests pass. The single rebuilt offline canonical
run fails before binding, at the unchanged native source-read work limit.
The positive canonical binding/cleanup assertions therefore remain unverified.

## Bounded change and regression

Only `canonical-fallback` was added to the valid-pair classification in
`FrontierAttemptPayloadPrimaryProbe.ps1`. It now selects the existing positive
count/hash/copy/repeat/authority checks. All original ownership checks, negative
cases, first-error preservation, and independent cleanup assertions are intact.
No production source, work budget, source/origin requirement, parent proof
expectation, or live route-acceptance rule changed.

The new regression in `FrontierPayloadPrimaryErrorTests.cs` parses the actual
probe and executes its classification expression, rather than replacing the
probe's expression with a test implementation. On both Windows PowerShell and
PowerShell 7 it checks ten cases: the three valid cases; foreign completion,
retired pair, retired owner, diagnostic control, uppercase canonical spelling,
unknown, and empty input.

Both tests first failed for exactly
`canonical-fallback; expected=True; actual=False`. After the one-line fix,
both pass, with zero failures/skips. This regression tests classification,
not native binding authority. The unchanged native and diagnostic suites from
the preceding checkpoint were not rerun as unrelated aggregate work.

Removing this step's one-line addition and added test method reconstructs both
starting files' SHA-256 hashes exactly. The focused diff check passes. The
non-incremental smoke rebuild succeeds in 23.47 seconds with zero errors and
357 warnings; none names the changed test/probe. Full warning details are in
`artifacts/primary-binding-classification-20260928/smoke-build.log`.

## Single canonical result

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-ad1266bbb1104b7ebf9c79f3356c7e2c`.

Exactly one rebuilt offline canonical test was run (3 minutes 15 seconds).
It fails at the original native reader before EOF:

```text
canonical-source-failure; reason=frontier_source_not_current; active_ms=20003; lines_read=72896
primary-source-currentness; rejected=resource:budget; base=none; close=ReadSource; exception=System.IO.IOException:frontier_source_not_current; work_ms=20001; lines=72896; units=68950876; started=True; ended=False
```

The available semantic input has 73,719 lines. The original reader returns
72,896, then refuses the next read. No origin batch executes, no complete
origin vector is produced, and no coordinator evaluation is reached
(`0/1`: zero evaluations, one native parser invocation). There is no
`analysis-observed-source.json`, binding result, or in-process binding cleanup
result. These are not zero-valued successes; those stages were never reached.
The outer strict incomplete-proof expectation also rejects this earlier failure.

The original cumulative clock reconciles exactly:

| Component | Seconds |
| --- | ---: |
| Attach | 0.0396701 |
| Open | 0.0010146 |
| Read | 19.9626265 |
| Total | 20.0033112 |

The first refusal occurs at 20,001 ms; the later diagnostic includes unwind
accounting. No time is excluded or subtracted to claim a pass.

| Original operation during reading | Calls | Seconds | Share of read work |
| --- | ---: | ---: | ---: |
| Base identity/liveness | 220,798 | 1.6958809 | 8.50% |
| Scope snapshot | 147,901 | 0.4694216 | 2.35% |
| File/facts currentness | 147,901 | 11.4788033 | 57.50% |
| Native-pin currentness | 147,901 | 5.8714016 | 29.41% |
| Mapped reads | 2,109 | 0.1062774 | 0.53% |

Remaining reader work is 0.3408417 seconds. Facts contain 1,928,535 bytes and
have no nested acknowledgment. These are elapsed observations, including
scheduling and profiling effects, not CPU-only timings.

## RCA and next bounded plan

The first missing edge is native source completion under the existing
20,000 ms cumulative limit. The original `ResourceCurrent` guard rejects
during `ReadSource`, before the classification expression can participate
in binding. This is not evidence that the classification change regressed
binding or that payload handles leaked.

Prior runs reached EOF with narrow remaining budget. Their inputs and
scheduling differ, so this is not a controlled performance regression
comparison. The latest profile identifies the whole file/facts validation
path as dominant, not a specific inner comparison. The previously implemented
exact `memcmp` optimization is still present; it must not be proposed again
as though absent.

Recommended next step:

1. Use the existing codec-free `FrontierFactsWorkControl` with the latest
   1,928,535-byte body and 147,901 checks to reassess the fixed offer, guarded
   body comparison, and full facts validation. It must not replay native
   authority or fabricate analyzer facts.
2. Review a minimal improvement only in the measured dominant implementation,
   preserving exact bytes, check ordering/frequency, lifetimes, cancellation,
   and the original cumulative limit. Do not tune the pin path merely because
   it is second-largest. Any proposal to change validation cadence requires a
   separate contract review rather than an implicit optimization.
3. Run directly affected mismatch/lifetime/budget controls, rebuild, and make
   one targeted offline canonical attempt. Require genuine EOF, all original
   origins, then the unchanged binding/cleanup and three-evaluation assertions.

No additional optimization or canonical retry is included in this checkpoint.
A live test is not yet the next step. Fallback completion, SHA matching, and
native custody still cannot replace the required second coordinator-published
`file_tuna_v4` route.

## Cleanup and preservation

Primary PID 49420 exits 1 without timeout. Offline GUI PID 53952 exits 0 with
complete output capture, no timeout/deadline extension, and 6,384.2903 ms active
work. Native PIDs 11204 and 51964 each report 268,435,456 bytes on
`post_tuna_fallback_v6`, with matching session/transfer identifiers.
All four processes are gone. Both payload files allow exclusive read/write
access and independently match SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
This proves post-run release, not the unexecuted in-process binding assertion.

Source/retained/writer/semantic line counts are
101,866 / 101,864 / 101,866 / 73,719. The analyzer input is unchanged before/after:
`9ab2845160f05b8553773f38a8bcff3f7c3e13ff6a5e00e0fb9f2a1f85e77483`.
Its receipt is failed, with no complete proof artifacts and parity missing.

Starting seal:
`artifacts/primary-binding-diagnostics-20260928/FINAL-VERIFICATION.json`,
SHA-256 `811e4f66e9ddd50b6d3e1aadb472ef88ba0ff5d5610838f8050a9ab8ecece870`.
All 8,315 pins matched before edits. The 438 starting dirty entries are
preserved; only the two scoped fixture/test files change, with this report
added. HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.

Observations, results, and final preservation audit are under
`artifacts/primary-binding-classification-20260928/`. No live GUI/NKN run,
portable build, full matrix, commit, or push occurred.

