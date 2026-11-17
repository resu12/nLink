# Original GUI work-cost measurement — 2026-09-27

## Outcome

Passive profiling identifies **final ledger capture plus recovery preparation**
as the largest measured interval: **6,474.4681 ms**, versus **2,683.2813 ms** for
the subsequent observed-ledger processing pass. The measurement does not yet
separate final ledger capture from recovery-marker scanning. No functional
optimization is justified at that finer level yet, and none was implemented.

One fresh offline canonical attempt passed role/source/destination coexistence
at **9,884.8055 ms** on the unchanged original 10,000 ms cumulative clock. It
then failed to connect its original payload offer because the wrapper listener
had already expired. Cleanup entry was **9,904.3888 ms**. This is not a fix for
the earlier 10,001 ms failure: approximately 96 ms remained, and the remaining
native handoff work was not exercised. No native-writer handoff or primary
analysis completed; integration and route-cycle acceptance remain failed.

## Scope and changes

Starting branch/HEAD: `v0.7.0` /
`a653d4063070235c71fe211480aeb37863d9b2a4`.
All 2,543 preceding pins matched before editing; the workspace had 393 dirty
entries. The preceding checkpoint is
`artifacts/role-join-rca-20260927/FINAL-VERIFICATION.json`, SHA-256
`28396a49f1b55ad9d47f7b64d1a814358eb0ea2c6a6a6a2a19f6145ef5616207`.

The passive helper in `tests/nLink.SmokeTests.Core/Support/OwnedSourceCheckProfile.cs`
samples the existing adopter/GUI Stopwatch, wall-clock ticks and running state.
It retains samples in memory and prints them after the existing cleanup. It
does not start, stop or reset the original clock, evaluate ownership, supply a
cached verdict, grant authority, or create another work allowance. The older
helper that repeats currentness checks 32 times is not used in these runs.

Checkpoints surround existing calls in:

- `Support/FrontierAttemptWritersJoin.ps1`: seal, processing, role controls and cleanup.
- `Support/FrontierAttemptSourceJoin.ps1`: existing source controls.
- `Support/FrontierAttemptDestinationJoin.ps1`: destination controls, coexistence and offer.
- `tools/FileTransferOps/LocalGuiWriterCursorMatch.cs`: four
  `NLINK_TEST_ATTEMPT_DISPATCH`-only samples within the existing processing path.

The two affected test classes check ordered, monotonic profile samples. The
default-loader control verifies the helper is absent. Rejected role joins omit
the successful-role-check checkpoint; their assertions reflect that existing
branch rather than requiring a checkpoint they cannot reach. No existing
authority predicate, short-circuit order, negative probe or cleanup decision
was removed. The normal loader compiles out the hooks in the tool source file.

## Verification

Ten unique focused controls passed, none skipped:

- Four: exhausted original-clock rejection and successful original native
  writer handoff, on Windows PowerShell and PowerShell 7.
- Two: normal-loader exclusion on both hosts.
- Four: original app/sequence mismatch rejection on both hosts, verifying the
  profile assertion's rejected-join branch.

The canonical integration ran once and failed; it is not counted as a passed
control. Results are in `artifacts/gui-work-cost-20260927/results/`.
It ran before the final test-only rejected-join assertion refinement. That
refinement did not change its executed test, support scripts or tool code;
only the affected mismatch controls were run afterward. No repeated canonical
attempt, aggregate lane or GUI matrix was used to seek a pass.

## Canonical measurements

Artifact directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-9de19531e6ff4e08b9d811b1aa46918b`.

The source has **101,909 lines / 92,196,742 bytes**. The retained, writer and
semantic outputs contain **101,907 / 101,909 / 73,760** references, respectively
(277,576 total). Original-clock deltas below are active elapsed time, not CPU
time. The samples are one observation, not a distribution or performance gate.

| Existing interval | Original active work (ms) |
| --- | ---: |
| Source seal | 475.5191 |
| Final ledger capture + recovery preparation | 6,474.4681 |
| Observed-ledger processing | 2,683.2813 |
| Writer/recovery/cursor matches | 29.7677 |
| Role negative probes | 17.1966 |
| Original role bind | 12.2441 |
| Role identity/copy/repeat controls | 96.7587 |
| Source bind controls | 19.8566 |
| Destination bind controls | 12.8985 |
| Source/destination coexistence | 20.5833 |

The largest interval accounts for approximately 65% of cumulative work at
cleanup entry. It spans `TryCaptureLedgerFinal()`,
`TryCaptureRecoveryMarkers()` and the small prefix/key preparation immediately
following them. These operations were grouped in the initial profile; do not
attribute the entire interval to either method individually.

Code inspection shows final capture synchronizes the original ledger under
original source checks; recovery capture scans the four original stdout/stderr
readers and checks existing work/currentness at its established boundaries.
Inspection alone does not identify the dominant sub-operation or permit fewer
checks. The earlier idea of avoiding an allocation in the main ledger line
reader was not implemented: that reader is in the smaller, later interval and
its allocation cost has not been isolated.

The successful smaller native-writer controls reached cleanup at 3,911.3677 ms
(Windows PowerShell) and 2,944.8493 ms (PowerShell 7). They establish the profile
can follow the full handoff path without granting authority, but are not
substitutes for this larger canonical case.

## Independent listener failure and retained evidence

The wrapper records `stage=connect`, `OperationCanceledException`,
`elapsed_ms=100001`, `lifetime_cancelled=False`, `work_ms=0` at
`2026-09-27T12:31:44.9764060Z`. The later original offer records a connection
timeout at 10,003 ms, at `2026-09-27T12:32:04.6809912Z`. Its approximately
10-second connection wait consumed only 19.5833 ms of original GUI active
work between coexistence and cleanup entry. These remain separate clocks.

The GUI exited with code 1, `timedOut=false`, within the outer scenario
deadline. GUI 56452 / sender 55584 / receiver 51112 are no longer present.
Both original apps report 268,435,456 bytes on `post_tuna_fallback_v6`, with
matching identity and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent source/destination hashes match, and exclusive access succeeds
after cleanup. At physical ledger lines 1029 / 1036, fallback state epoch 2
applies from previous epoch -1 with credit 512; its repeat is duplicate and
unapplied. The earlier runtime epoch fix is unchanged.

Exact profiles, hashes and observations are retained in
`artifacts/gui-work-cost-20260927/canonical-observation.json` and
`control-observations.json`. Payload correctness does not establish the second
coordinator-published `file_tuna_v4` or replace primary analysis.

## Next bounded step

Split the existing pre-parse profile at the return of final ledger capture and
recovery-marker capture, without another currentness evaluation. Use those
measurements to select one exact, behavior-preserving cost reduction, retaining
every original identity, byte, cancellation and ownership check. If none is
justified, present the contract tradeoff rather than changing the budget or
moving work outside its clock. Then verify only the affected guards and one
targeted offline canonical attempt.

The separately reviewed listener-lifetime correction remains necessary, but
does not remove the cumulative-work risk or prove remaining handoff headroom.
Keep its 10-second connected exchange, 5-second wrapper-work limit and original
owner cancellation/exit checks. No timer, runtime authority, transport tuning,
acceptance rule or GUI behavior was changed in this measurement step.

This checkpoint is sealed in
`artifacts/gui-work-cost-20260927/FINAL-VERIFICATION.json`.
No live NKN test, portable rebuild, full GUI matrix, commit or push occurred.
