# Parent fixture layout and original-failure diagnostics

Date: 2026-09-27. Status: fixture correction verified; the single rebuilt offline
canonical attempt stopped at the earlier parent take boundary.

## Implemented scope

Only the explicit offline attempt fixtures changed:

- `FrontierAttemptPayloadParentProbe.ps1` selects `native-carrier-received` for
  the declared full carrier case and `native-received` for the ordinary case,
  matching the existing destination fixture. It does not fall back based on
  directory existence. It requires exactly one received `.bin` file and an
  existing source file before checking both files' exclusive-open rejection.
- `FrontierAttemptDispatch.ps1` prints the already-caught exception type and
  message before cleanup or the expected-incomplete-proof assertions can mask
  it. The caught value and all acceptance assertions remain unchanged.
- `FrontierPayloadParentBoundaryTests.cs` exercises the exact filesystem block
  extracted from the fixture AST, plus its actual catch and acceptance guard.
  Small files with real held file streams exercise sharing behavior only;
  these tests neither mock nor establish native custody/currentness.

No runtime authority, transport, ownership, timer, work allowance, evaluator
count, route contract or publication behavior was changed. In particular, the
parent currentness check still precedes filesystem probing. No additional
authority/currentness evaluation was added for diagnostics.

## Verification

Artifacts: `artifacts/parent-fixture-layout-20260927/results`.

| Check | Result |
| --- | --- |
| New focused controls against old fixture | 12 passed, 22 expected failures |
| Same controls after correction | 34 passed, zero failed/skipped |
| Genuine ordinary parent handoff, both PowerShell hosts | 2 passed |
| Smoke assembly rebuild | Zero warnings/errors |
| One rebuilt offline full-carrier canonical attempt | Failed at parent take |

The 34 controls cover both hosts and both layouts: protected pair, missing
directory, empty/ambiguous receiver inventory, only the wrong layout present,
missing source, and source/receiver separately unprotected. Diagnostic controls
also require the original exception to remain visible while preserving both
the incomplete-proof rejection and canonical-source stop rules.

The first baseline command's logger argument was accidentally unquoted, so
PowerShell split it at the semicolon. The intended 34 tests still ran and wrote
the default TRX; that unchanged TRX was copied to `parent-boundary-before.trx`.
The trailing logger-path command failed harmlessly. Subsequent commands quote
the logger argument. No test was repeated to repair the baseline filename.

The previously completed 52 wrapper/lifetime checks were not rerun. No unrelated
aggregate lane was run.

## Latest canonical evidence and first missing boundary

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-e77cb1c4f6e74a0f9dad288d0845fa37`.

The GUI completes native-writer handoff and exits 0 with output capture complete,
no timeout and no deadline extension. Its cumulative active work at cleanup is
6,270.5705 ms, below the unchanged 10,000 ms allowance. Wrapper verification,
the parent payload offer and wrapper completion markers are present.

Both native apps report 268,435,456 bytes on `post_tuna_fallback_v6`, with matching
session, transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent post-run checks confirm both file lengths/hashes and successful
exclusive read/write opening. GUI PID 30732 and native PIDs 48540/50680 are no
longer running.

The new diagnostic retains:

```text
attempt-dispatch-failure; type=System.Management.Automation.RuntimeException; message=Parent association outcome/repeated take changed
```

This comes from the existing `Take`-stage assertion after
`TryTakeAfterExitForTest`, before `AnalysisBoundary`. Cleanup's final thrown
message is `Parent handoff not exercised`, but the original failure is now
preserved. No primary worker copy, stdout or receipt exists. This attempt did
not reach the corrected filesystem probe or primary analysis.

Read-only source inspection identifies the compound outcome/repeated-take
assertion as the first observed refusal. The artifact does not retain the
underlying guard that returned null (or separately label the repeated-take
result). `TryTakeAfterExitForTest` includes original identity/report, wrapper
exit/output, channel commit, payload-file, original native-relay association,
work-budget and lifetime checks. This run does not establish which predicate
refused. Do not diagnose a timer or ownership bug from the aggregate message.

This is earlier than the boundary seen in the preceding run. The new path
selection runs only later, so this artifact does not show that the path edit
caused the take failure. Likewise, it cannot recover the original masked
exception from the preceding artifact. The directory defect is fixed and
directly tested; full canonical validation remains blocked.

## Next bounded step

Instrument the existing parent take path to retain its first refusal using
results from the original evaluations, without repeating them. Distinguish the
first take's result from the repeated-take assertion, and preserve the existing
cleanup, return values, one-shot semantics and work allowances. Add focused
controls for those diagnostics, then rebuild for one targeted offline attempt.
Do not add retries, change runtime behavior or widen any timer from this run.

No further canonical attempt was made in this step. A full matrix or live-256
test is not yet appropriate. Payload/SHA/handoff success is not the required
second coordinator-published `file_tuna_v4` route.

## Recovery seal

Preflight verified all 4,746 prior pins at HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`, with 408 existing
dirty entries. Prior manifest SHA-256:
`5970b77f9417bc46769e74eff2bfa17636d499416e676431650061abb369fd63`.
The preservation audit found only the two intended fixture-script edits and
rebuilt smoke DLL changed among those pins. Existing dirty work and prior
evidence were preserved.

Current recovery seal:
`artifacts/parent-fixture-layout-20260927/FINAL-VERIFICATION.json`.
No live GUI/NKN run, portable rebuild, full matrix, commit or push was performed.
