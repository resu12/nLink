# Late-publication characterization: first scheduling gate unavailable

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result

Ran exactly one rebuilt, small offline original-process characterization:
`OriginalApps_CarryLateFrontierReceiptIntoPrimary`. **Failed: one test, zero
passed, 39.130 seconds.** No automatic retry. The first failing edge was
`AttemptLatePublicationWindow.Create`, before owner creation or registration:

```
offline_schedule_unavailable; stage=native-arm-and-original-hold-budget
  late_publication_original_budget_unavailable
```

No publication-delay owner armed, selected-copy delay ran, native late receipt
was established, or primary analysis started. This is an unavailable offline
scheduling profile, not evidence of a production route-authority defect or an
accepted transfer. No completed destination or primary output was produced.

The rebuilt Debug harness launches native peer processes loading the real Core
and transport through the local named-pipe carrier. The transfer profile is
172,032 bytes (eight 21 KiB chunks). The unchanged parent still creates its
canonical 256 MiB source and uses GUI/live-cycle labels in wrapper logs; those
labels do not make this a live NKN, portable GUI, or live-256 test. The portable
package was not rebuilt or exercised.

## Evidence and first-edge RCA

Run directory:
`artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-df80a3ec28c74411a397f45500ef530b/`.
Build, preflight, one-run marker, TRX, log and preservation seal:
`artifacts/late-publication-characterization-20260928/`.

The helper stderr identifies `AttemptLatePublicationOwner.cs:31`, called at
`AttemptOfflineLatePublicationProgress.cs:104`. The preceding shape guard passed;
the following owner constructor, transport registration and chunk-one release
were not reached. At least one of these unchanged predicates failed:

- `now >= S.CounterAfter + 250 ms` (arm too late).
- `G + 200 ms >= holdEnd` (original data-hold reserve unavailable).
- `G + 100 ms >= min(S.CounterBefore + 1250 ms, holdEnd - 100 ms)`
  (absolute expiry/completion allowance unavailable).

Here G is `S.CounterAfter + 800 ms`; `holdEnd` is the conservative translation
of the oldest unchanged 3000 ms data-hold deadline. The exception records one
generic reason for all three predicates, without their raw QPC/TickCount64
inputs. The exact predicate, or combination, is therefore not recoverable from
this capture. No synthesized native timing receipt or post-run evaluation was
used to fill that gap.

The original operational log supports this sequence (UTC):

| Physical log line | Observed edge | Time |
| --- | --- | --- |
| 1050 | First ordinary sparse write, highest 3; writer-attempt timestamp | 14:47:38.1925091 |
| 1243 | Original repair progress/recent-grace start | 14:47:39.4151443 |
| 1314-1315 | Released sparse chunk 2; frontier stays 1/highest 7; state deferred | writer attempt 14:47:39.7354592 / .7364031 |
| 1386 | Original frontier-1 stall start; deadline S+750 ms | 14:47:40.4240499 / 14:47:41.1740499 |
| 1388 | Original recent-progress recheck, `stall_grace_armed` | writer attempt 14:47:40.4265203 |

Inference only: ordinary chunk 1 was held before chunk 3 was delivered. Assuming
the captured UTC and monotonic clocks tracked normally, the timestamps put the
oldest hold expiry no later than approximately 14:47:41.193, versus the required
G+200 ms at approximately 14:47:41.424. This suggests a hold-reserve shortfall of
at least roughly 0.23 seconds. Writer-attempt timestamps are not original hold
start/QPC captures, so this is not an exact monotonic budget proof, nor does it
exclude a late arm as well.

The sender subsequently timed out waiting for the destination-verified event.
The wrapper's `Original Core writer shutdown incomplete` check also requires
successful app requests and handoffs, not merely exited/drained writers. Its
generic failure is secondary; it is not evidence that writer processes leaked.
Retained analysis correctly did not start.

## Verification and cleanup

- Entry and isolated preflight matched all 1,977 prior pins and 508 dirty files.
  The Debug build passed with zero warnings/errors in this incremental build;
  pinned binary hashes did not change. The previous 112 focused checks remain
  the checks for unchanged code; they were not rerun or counted as native proof.
- The original-process run and process inspection used explicit escalation.
  An initial read-only preflight stopped on inherited `NLINK_*` settings before
  any test. The runner then isolated these only in its process environment;
  no user/machine setting or wallet value was modified or logged. Debug overrides
  were available with developer mode false; QPC frequency was 10,000,000.
- `attempt-started.json` was created with `FileMode.CreateNew` before the test,
  preventing an automatic second attempt. No retry, timing sweep, analyzer
  replay, or force-release was performed.
- Both writers emitted their original drain markers (physical lines 1581/1594).
  Finalization records child exit code 1, output capture complete and no timeout.
  At 14:57:50 UTC, peer PIDs 42976/41664 and wrapper PID 52272 were absent; only
  the four pre-existing MSBuild servers remained among relevant processes.
- All 18 run files opened read-only with exclusive sharing at 14:57:51 UTC.
  Both receive directories were empty. Source inspection confirms the failure
  disposes/clears the data schedule; the publication owner was never created.
  No in-process buffer-count observation is claimed after process retirement.
- No production/test source, acceptance rule or timer was changed. This turn
  adds this handoff and ignored runner/evidence files only. No commit or push.
  The final independent preservation check records prior pins/dirty content
  unchanged and this handoff as the sole new non-ignored file.

## Recommended next bounded step

Add test-profile-only failure diagnostics at the existing window check: retain
the exact immutable clock/hold inputs, derived deadlines/margins and separate
predicate results, without changing the decisions, owner lifecycle or timing.
Extend only the directly affected window guard tests to verify those reasons,
including equality boundaries and multiple failed predicates. Do not edit the
production coordinator, transport policies, analyzer acceptance or existing
positive profile.

Review feasibility using those diagnostics before proposing another single
rebuilt small run. Keep S+250/S+800, all original 750/1000/3000 ms bounds and the
completion reserves unchanged. If those bounds cannot accommodate the original
episode, report the unavailable profile and review a contract change separately;
do not silently extend holds, select a later episode or retime production work.
No further characterization, live-256 or full GUI matrix has been started.
