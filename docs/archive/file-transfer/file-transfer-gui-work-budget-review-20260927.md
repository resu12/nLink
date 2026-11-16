# GUI active-work budget review — 2026-09-27

Status: **review complete; proposed budget change not implemented or approved**.
The user accepted the processing-custody implementation as a milestone with
known Windows failures, then authorized this budget review. That is not a
retroactive pass for either failed test or approval of a particular new limit.

Baseline: `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
All 1045 pins in the preceding implementation checkpoint verify unchanged;
its SHA-256 is
`c2c5f88101e3f61f15fd97611c1a97cb099bb2760a0abf0135101e33035e9977`.
This review changes documentation only. No tests, runtime code, acceptance
rules, clocks, packages, commits or remote branches are changed.

## Recommendation

Approve a **fixed 10000 ms cumulative GUI active-work budget**, replacing only
the current 5000 ms bound on the original shared GUI/adopter stopwatch. Apply
the same policy on Windows PowerShell and PowerShell 7, not a shell-version
exception, environment override, caller-supplied timeout or test-only bypass.

This changes the permitted resource-work duration, not what constitutes valid
ownership, native writer association or completion. It is a production/tool
contract adjustment for this GUI custody path, not merely a looser test
assertion. Standalone uses of the same internal adopter primitive would use
the same fixed budget; their independent test expectations must follow it.

Ten seconds is a proposed bounded engineering allowance, **not a measured
minimum or a guarantee of success**. Both failing observations are censored
by the existing deadline, so their eventual completion times are unknown.
There is no evidence that adding one or two milliseconds would suffice.
Doubling this one cumulative allowance provides headroom without changing
the independent parser/parent limits or the existing 20-second rendezvous.
No automatic increase beyond ten seconds is authorized if a test still fails.

## What the clocks protect

These are different clocks and must not be replaced as a group:

| Clock / owner | Current limit | Proposed limit / treatment |
| --- | ---: | --- |
| GUI `frontierWork`, passed as the adopter's original `work` | 5000 ms cumulative measured work | **10000 ms**, same object, accumulated intervals and checks. |
| Streaming processor's local `clock` | 5000 ms | Unchanged; even with remaining GUI budget, local parser expiry rejects. |
| Parent registration's `parentWork` | 5000 ms cumulative measured work | Unchanged; GUI headroom cannot rescue an expired parent. |
| Parent rendezvous's `parentRendezvousClock` | 20000 ms wall time (`ProducerMilliseconds`) | Unchanged, one attempt, no renewal. |
| Legacy completion encode/publish checks | 250 ms (`MaximumCompletionMilliseconds`) | Unchanged. |
| Native input capture | 2000 ms (`NativeMilliseconds`) | Unchanged. |
| Writer/membership input work and other primary/relay/diagnostic clocks | Their existing limits | Unchanged. |

The GUI owner creates `frontierWork` in `LocalGuiWriterLedgerOrigin.cs` and
passes that exact stopwatch to `LocalFrontierFileAdoption` in
`LocalGuiWriterFrontierFiles.cs`. Ledger creation/synchronization, adopted-file
verification, source sealing, processing, semantic copy, persistence and GUI
parent-handoff checks draw from this shared accounting. The processing interval
remains running; later original operations restart the same stopwatch without
resetting its accumulated duration. Interprocess rendezvous waits remain outside
active-work accounting and are fenced by their existing wall deadline.

"Active work" here means elapsed time during measured intervals, including I/O
and scheduling delays while running; it is not measured CPU time. Checks are
cooperative, before/after operations. Neither five nor ten seconds guarantees
that a blocked OS call is interrupted precisely at that time. This proposal
does not add or change a watchdog.

The cumulative limit bounds work and resource retention in a native custody
pipeline. It is not a throughput target or a coordinator route-ownership proof.
Relaxing it permits a still-current operation to retain resources longer and
consume more work. Cancellation, process-death, disposal, mapping, identity and
file-custody checks must continue to reject immediately when next evaluated.

## Evidence and acceptance distinction

The latest original offline 256 MiB run has 217 passing focused/compatibility
controls and two passing PowerShell 7 full-size cases. Windows PowerShell:

- Positive: copy 3060 ms, persistence 3516 ms, preparation accepted at 4942 ms,
  completion rejected at 5001 ms.
- Foreign writer: copy 4197 ms, persistence 4253 ms, preparation rejected at
  5002 ms; facts attempted, ACK and completion not attempted. The intended
  foreign-writer assertion was never reached.

PowerShell 7 completed at 3330 ms and 3367 ms. All eight sent/received full-size
files were independently reopened exclusively after the runs. Cleanup and full
file length do not establish native parent completion or foreign-writer rejection.
The current Windows results remain failures. Their exact artifacts and result
names are in `file-transfer-processing-custody-implementation-20260927.md`.

The minimum acceptable new positive result is the complete original native
payload/writer association and successful completion/exit/take. The negative
case must reach and pass its exact foreign-origin rejection. Merely avoiding
timeout, transferring 256 MiB, obtaining SHA OK or observing cleanup is not enough.
The full-size runs remain offline DevLocal evidence, not live route acceptance.

## Bounded implementation, if separately approved

1. Introduce one internal fixed `MaximumActiveMilliseconds = 10000` constant in
   `LocalFrontierFileAdoption`. Use it for the six existing GUI/adopter budget
   comparisons: two in adopter `Current`, one in `BufferedWorkCurrent`, and
   three in ledger creation/currentness. The production edit surface is
   `LocalFrontierFileAdoption.cs`, `LocalFrontierFileSeal.cs` and
   `LocalGuiWriterLedgerOrigin.cs` only, unless review discovers another alias
   of this exact stopwatch. Do not replace unrelated `5000` literals.
2. Preserve the same stopwatch, start/stop ownership and nested-work behavior.
   Do not reset the timer, grant a fresh budget per stage, exclude previously
   charged work, or split ten seconds into renewable phases. No change to
   locks, read caps, custody sample schedule, caller boundaries or retry rules.
3. Audit the budget fault fixtures by **which clock they mutate**. GUI/adopter
   expiry injections currently using six seconds must exceed the new limit;
   local parser, parent and unrelated six-second expiry controls must stay.
   Also update GUI-specific five-second success assertions, not every textual
   occurrence. Pin the proposed value independently so deriving fixtures from
   a constant cannot silently accept an arbitrary later increase.
4. Run directly affected budget/cleanup controls once after stabilization:
   shared-clock identity and accumulation; still-current GUI custody beyond
   five but below ten seconds; rejection beyond ten before/after reads and
   finalization; stopped clock and cancellation; unchanged parser and parent
   expiry at their existing limits; unchanged copied/foreign and completion
   rejection. Exercise relevant script controls on both PowerShell hosts.
   Use deterministic elapsed-time injection, not sleeps or assertions on host
   speed. Existing invalid-object checks must still reject within the extended
   time window, rather than being accidentally masked by an old expiry value.
5. Rerun **only the two failed Windows PowerShell full-size cases**, once, using
   the current original process-tree fixture. Record actual cumulative times,
   reached assertions and independent cleanup. Preserve failures. Do not retry
   until a favorable timing sample appears, waive missing proof, or extend
   another clock if the first missing edge moves.

No broad suite, full GUI matrix or live test is required for this review. A later
live rerun still requires its normal focused gate, portable rebuild and
unsandboxed preflight. The second coordinator-published `file_tuna_v4` remains
mandatory; Regular/Tuna protocol 4 and post-Tuna fallback protocol 6 remain
unchanged, and active Tuna protocol 6 remains invalid.

## Approval boundary and recovery

The decision requested is **10000 ms for the one shared GUI/adopter cumulative
work budget on both hosts, with every independent limit and correctness gate
unchanged**, followed by focused budget controls and the two failed Windows
offline reruns. Approval would supersede only the prior requirement to keep
this particular clock at five seconds; it would not authorize a broader parent
validation optimization or acceptance waiver.

If not approved, retain the implementation milestone with its two documented
Windows failures and the existing five-second bound. No end-to-end pass or
live readiness is claimed in either case without successful required evidence.

All 314 baseline dirty files remain byte-identical. The review and its recovery
inventory are sealed in
`artifacts/gui-work-budget-review-20260927/FINAL-VERIFICATION.json`.
