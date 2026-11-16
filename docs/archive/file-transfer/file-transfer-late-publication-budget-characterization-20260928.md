# Late-publication characterization: measured hold-budget conflict

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result and decision

Ran the approved **single 168 KiB offline original-process characterization**
after rebuilding and passing isolated preflights. Result: one failed test,
zero passed, 35.501 seconds. No retry. The new diagnostics establish the first
failing condition: **insufficient original hold reserve, not late arming**.

The unavailable-window guard stopped before creating the publication owner or
registering either delay hook. No delayed publication, native late receipt,
completed destination, primary analysis, selected failure or diagnostic output
was established. This is an unavailable test schedule, not a production
route-authority RCA or accepted transfer.

The next step is a bounded **test-contract feasibility review**, not another
run or a production fix. Keep the current code, timers and acceptance unchanged
until a reviewed alternative has been separately approved.

## Exact captured budget

Original evidence:
`artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-6f37e470add047c586ac19e58f9e5c6d/process-output/helper-22792.stderr.log:4`.

Its nested exception reports
`offline_schedule_unavailable; stage=native-arm-and-original-hold-budget`, then
`late_publication_original_budget_unavailable; budget_json=...`.
The original JSON is copied byte-for-text into `raw-budget.json` under
`artifacts/late-publication-budget-characterization-20260928/`; its integer
clock fields are preserved without floating-point conversion. The separate
`budget-analysis.json` records only derived arithmetic and source references,
not a recreated native receipt or authority evaluation.

QPC frequency is 10,000,000 ticks/second. Relative to the original stall's
`CounterAfter`:

| Measured quantity | Milliseconds |
| --- | ---: |
| Guard check after stall | 4.5567 |
| Remaining arm allowance to S+250 | 245.4433 |
| Original conservative hold end after stall | 676.0408 |
| Hold remaining at the guard check | 671.4841 |
| Planned all-copy release after stall | 800.0000 |
| Hold-reserve margin (hold end minus G+200) | -323.9592 |
| Absolute-expiry margin (expiry minus G+100) | -323.9592 |

The original record contains `LateArm=false`, `HoldReserveUnavailable=true`,
and `ExpiryAllowanceUnavailable=true`. Two original holds remain, totaling
44,566 retained bytes. Both deadline arithmetic and all three predicate flags
were checked against the captured inputs without calling any app evaluator.

The third predicate fails for the same hold-limited absolute expiry; it is not
evidence of another independent runtime fault. The nominal earliest late
completion is D=S+750 ms. The measured conservative hold end is already about
73.9592 ms before D (the stall capture bracket is one QPC tick). Thus moving G
closer to D, or merely removing the G+200 reserve, cannot make this measured
episode fit the current conservative contract. This does not prove that every
possible future episode has the same timing; repeated races are not acceptance.

The original log retains frontier 1/highest 7, sparse state deferral (physical
line 1307), original stall/deadline at 15:30:27.8476984/28.5976984 UTC (line 1381),
and recent-progress recheck `stall_grace_armed` (line 1384). The predicate result
now comes from original monotonic budget inputs, not the older UTC inference.

## Preflight, cleanup and preservation

- Entry and post-build preflight matched all 2,013 saved pins and 510 existing
  dirty entries. The incremental Debug build passed with zero warnings/errors;
  the pinned binaries were unchanged. The existing 29 focused window checks
  were validated from their pinned TRX, not rerun or claimed as native proof.
- The native-process harness ran outside the restricted sandbox with explicit
  escalation. No conflicting process was present; developer mode was false.
  Inherited `NLINK_*` settings were isolated only in the runner process, without
  logging secret values or modifying user/machine settings. The fresh
  `attempt-started.json` uses create-new semantics to prevent a second attempt.
- The unchanged parent creates a canonical 256 MiB source and retains GUI/live
  wrapper labels, but this run used the small Debug named-pipe carrier. It was
  not live NKN, portable GUI, live-256 or the full matrix. No portable rebuild
  or network acceptance is claimed.
- The sender's destination-verification wait and wrapper's successful-writer
  custody check failed secondarily. Primary analysis correctly did not start.
  Both original writers emitted drain markers (log lines 1572 and 1585).
  Wrapper finalization records exit code 1, output capture complete, no timeout.
- At 15:32:51 UTC, peer PIDs 49640/22792 and wrapper PID 34596 were absent; no
  conflicting process remained. All 18 run files subsequently opened read-only
  with exclusive sharing. Both receive directories were empty. Cleanup checks
  killed no process. No retired in-process buffer observation is claimed.
- No source, test, binary, runtime timer, acceptance rule, or earlier evidence
  was changed. Only this handoff and ignored runner/evidence files were added.
  The final independent seal checks preservation. No commit or push.

## Next review scope

Review whether a different **test-only preconditioning/packet schedule** can
reach the same original repair/recent/stall/first-write chain with enough of the
unchanged hold budget left. Retain the same original source/observation/root,
required late receipt, physical-copy checks and primary/output custody.
Do not move production work earlier, manufacture a receipt, suppress a competing
state, reset a hold clock or choose a later episode to force a witness.

If that cannot be demonstrated, propose an explicit isolated fixture-contract
revision with its proof limits and obtain approval before implementation. Do not
silently extend the 3000 ms holds, change S+250/S+800 or native 750/1000 ms bounds,
relax reserves or weaken acceptance. No further characterization, timing sweep,
live-256 or full GUI matrix should precede that review.
