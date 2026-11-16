# Recovery preparation cost — 2026-09-27

## Outcome

The measured preparation hotspot is **recovery-marker capture**, not final
ledger capture: **5,928.1795 ms** versus **1,219.3592 ms** in one fresh offline
canonical attempt. Recovery capture finished; the original 10,000 ms GUI
cumulative budget then expired during observed-ledger processing. The run
remains failed, with partial outputs and no native handoff or primary analysis.

A separate diagnostic read of the closed, retained process outputs took
**12.9712 ms** in the same recovery parser using a counting callback rather
than original custody checks. This points toward validation/owned-reader
overhead, but does not measure the individual original checks. No functional
optimization, validation-frequency change or allowance increase was made.

## Bounded implementation and verification

Starting HEAD/branch: `a653d4063070235c71fe211480aeb37863d9b2a4` / `v0.7.0`.
The previous checkpoint had 395 dirty entries and 2,736 pins, all verified
unchanged before editing. Its seal is
`artifacts/gui-work-cost-20260927/FINAL-VERIFICATION.json`, SHA-256
`abfe423bac37914798171fb85ada23a0e8c5674687a551f7dfba9af3c77872e5`.

Two existing files changed:

- `tools/FileTransferOps/LocalGuiWriterCursorMatch.cs`: two test-only passive
  clock samples, immediately after the existing final-capture and
  recovery-capture calls. Both calls still execute in their original order,
  including when final capture returns null. The existing combined rejection
  predicate follows them unchanged.
- `tests/nLink.SmokeTests.Core/FrontierPayloadWriterTests.cs`: require both new
  checkpoints in the already-checked ordered, monotonic profile.

The helper and original work clock are unchanged. No new ownership check,
verdict cache, clock start/stop/reset, output authority or work allowance was
introduced. Normal-loader code excludes the samples.

Four focused controls passed, none skipped: successful original native-writer
handoff and default-loader exclusion on Windows PowerShell and PowerShell 7.
The canonical integration ran once and failed. No unrelated coordinator,
analyzer or aggregate lanes were rerun for this diagnostic-only change.
The run's exact changed-source and Smoke assembly hashes are retained in
`artifacts/gui-preparation-cost-20260927/observations.json`.

## Original canonical profile

Directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-d8e469c39f5449389a5d6efb8dd716ca`.

| Existing interval | Original active work (ms) |
| --- | ---: |
| Final ledger capture | 1,219.3592 |
| Recovery-marker capture | 5,928.1795 |
| Prefix/key preparation | 0.0771 |
| Processing attempt through rejection | 2,355.8798 |

These are elapsed-work samples, not CPU measurements or a performance gate.
The last interval is incomplete processing plus its rejection path, not the
cost of a successful full parse. There is no `processing_after_parse` sample.
The budget was already at 7,645.6540 ms immediately before parsing.

The failure-only diagnostic reads:

```text
payload-writer-processing-failed; active_ms=10001; source_bytes_read=73728000
```

The sampled stopped clock is 10,001.5338 ms. Source size is 92,048,257 bytes /
101,866 lines. Retained/writer/semantic files contain 81,247 / 81,248 / 58,732
readable lines, respectively. These are **partial rejected outputs**, not
validated captures or evidence eligible for analysis.

The prior canonical reached coexistence at 9,884.8055 ms; this run exhausted
the same clock earlier. This variation reinforces that the prior near-boundary
result did not resolve the cumulative-work risk. It does not establish the
cause of each historical intermittent failure.

## Standalone parser diagnostic and limits

`artifacts/gui-preparation-cost-20260927/Measure-RecoveryParserCost.ps1` invokes
the existing bounded parser on the four closed capture files, once per file,
with a counting-only callback. It never re-evaluates an original owner or
binds a recovery match. It uses ordinary reopened FileStreams and name-sorted
file order, not original role/channel reader custody or the original clock.

It reads 1,640,584 bytes / 13,137 lines, discovers zero recovery markers, and
invokes the counting callback 13,316 times. Total measured Read time is
12.9712 ms, including cold first-read/reflection overhead. It provides no
acceptance, ownership or cancellation proof. Do not subtract it from the
original interval and label the difference as an exact custody-check cost:
stream implementations, call context and timing differ.

Code inspection explains the leading hypothesis. `FrontierRecoveryMarkerScan`
invokes its check for every accepted physical line, including ordinary lines
without recovery markers, and around read/decoder work. In original capture,
the callback is `CheckRecoveryWork`, which evaluates binding currentness and
the original source's `ProcessingWorkActive`; those traverse native ownership
checks. The original readers also validate custody at their read boundaries.
The combined cost has not yet been apportioned between these paths.

Therefore changing the parser's string/byte mechanics is not presently a
justified remedy for seconds of recovery-capture cost. Nor does absence of
markers permit skipping the original scan or trusting retained text as proof.

## Failure and cleanup evidence

The independent wrapper listener again expired at 100,004 ms with
`lifetime_cancelled=False`, `work_ms=0`. No payload offer was reached in this
run. Its expiry and the GUI's cumulative-work exhaustion are separate failures.

Both original apps completed 268,435,456 bytes on `post_tuna_fallback_v6`, with
matching transfer/session and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent source/destination hashes agree; both allow exclusive access after
cleanup. GUI 33204 / sender 46824 / receiver 2868 have exited. The GUI exit is
1, `timedOut=false`, within the outer scenario deadline. Original fallback
state evidence is retained at physical source lines 988 / 994.

Payload completion does not establish native evidence handoff, primary
analysis or a second coordinator-published `file_tuna_v4`. Acceptance remains
unchanged and failed.

## Next bounded step and tradeoff

1. Attribute the existing recovery callback cost without replaying it: record
   aggregate elapsed ticks and call counts around the **already-executed**
   `CheckRecoveryWork` body, including its throwing path. Keep its predicate,
   short-circuit order and call sites unchanged; report after existing cleanup.
   Contrast this with total capture time to distinguish callback work from
   owned-reader/setup/match overhead.
2. Choose a cheaper exact operation only if that measurement identifies one.
   Preserve every native/identity/byte/cancellation check and original clock.
   Validate the directly affected controls before one targeted offline attempt.
3. If the dominant cost cannot be reduced under those constraints, explicitly
   review an alternative validation-boundary contract before implementation.
   Replacing per-line native checks with buffered rejection-only guards would
   alter revocation-observation timing even if full checks remain at read and
   handoff boundaries; it is **not** an equivalent optimization and is not
   approved or implemented here. Raising the work budget is also out of scope.

The independent listener-lifetime correction remains necessary afterward;
preserve the original cancellation/exit conditions, connected-exchange limit
and wrapper-work limit. No live test is due from this failed offline checkpoint.

Recovery is sealed in
`artifacts/gui-preparation-cost-20260927/FINAL-VERIFICATION.json`.
No runtime authority, timer, transport policy, acceptance rule, portable
package, live network, full GUI matrix, commit or push was changed/run.
