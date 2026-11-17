# Shared GUI active-work budget implementation — 2026-09-27

Status: **bounded implementation verified**. All 102 distinct focused checks
and both approved Windows PowerShell offline 256 MiB reruns pass. This closes
the two Windows GUI-work-budget failures, not live route acceptance.

## Authority and scope

The user approved the recommendation in
`file-transfer-gui-work-budget-review-20260927.md`: raise only the original
shared GUI/adopter cumulative active-work allowance from 5000 to 10000 ms on
both Windows PowerShell and PowerShell 7, run focused regression checks, then
rerun the two failed Windows PowerShell offline 256 MiB cases once.

Baseline: branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`. The preceding review checkpoint
verified all 330 pins, with no mismatch; its SHA-256 is
`1ead704f580de03f3b7ff26293a8136739acc4975518643a2db1e2b471354ece`.
The historical review remains unchanged as the pre-approval record.

## Change

One internal constant, `LocalFrontierFileAdoption.MaximumActiveMilliseconds`,
is fixed at **10000**. It replaces exactly six comparisons in three production
tool files: two in adopter `Current`, one in `BufferedWorkCurrent`, and three
in ledger creation/currentness. The original GUI `frontierWork` and adopter
`work` remain the same object. Start/stop ownership, accumulated intervals,
custody checks, locks, reads, cleanup and cancellation are unchanged.

No host-dependent limit, caller override, timer reset, per-stage allowance,
cached verdict or retry was added. Ten seconds is the approved resource-work
policy, not a proven minimum or a throughput target.

Independent limits remain unchanged: parser 5000 ms, parent work 5000 ms,
parent rendezvous 20000 ms, completion 250 ms, native capture 2000 ms. Nineteen
unchanged source/acceptance guard pins were independently verified. The
rendezvous fixture's stricter check that idle waiting consumes less than five
seconds of active work is deliberately retained; it is not an expiry injection.

## Focused regressions and fixture maintenance

GUI/adopter expiry injections were changed from six to eleven seconds only
after tracing their stopwatch identity. Parent, parser and unrelated clocks
retain their existing injections. The processing success assertion uses ten
seconds. Numeric policy checks independently pin 10000 rather than deriving
the expected policy from the implementation constant.

The new primitive regression first failed against the old five-second code at
its six-second admission check (`budget-red.trx`). It now checks the original
clock, nested retained-file checking, accumulated six- and nine-second work,
and rejection at eleven seconds. Native controls on both hosts cover original
source reads/finish at six seconds, post-finish custody at nine, retirement and
closed retained handles at eleven, and copies/foreign objects, cancellation,
stopped clocks and native GUI corruption within the extended valid interval.

The directly affected app-report fixture initially failed on both hosts before
its budget assertion: it extracted `Start-AppInstance` without the existing
`Set-GuiPreparationProcessIdentity` helper now called by that function. The
fixture import list now includes the exact original helper, following the
existing scenario-launch fixture. No production helper or launch logic changed;
the test does not stub or bypass the helper. Original failures are preserved.

Results under `artifacts/gui-work-budget-20260927/results/`:

| Run | Result | Purpose |
| --- | --- | --- |
| `budget-red.trx` | 1 expected failure | Original five-second rejection of the approved extended interval. |
| `budget-core.trx` | 36 passed, zero failed/skipped | Primitive/native expiry, extended custody, copies, cancellation, stopped clock, parser-local expiry. |
| `budget-handoff.trx` | 62 passed, 2 fixture failures, zero skipped | Affected GUI stages, unchanged parent expiry and completion controls; the two app-report import failures are retained. |
| `app-report-fixture.trx` | 4 passed, zero failed/skipped | Corrected app-report budget case and positive control on both hosts; no unrelated cases rerun. |
| `payload-writers-windows.trx` | 2 passed, zero failed/skipped | Original positive native-writer handoff and exact foreign-origin rejection, each run once. |

There are **102 distinct passing focused checks** after the targeted fixture
correction. All 21 edited PowerShell fixtures parse successfully; `git diff
--check` passes. No aggregate contract/analyzer/fake/smoke lane was run.

## Windows full-size results

Both original offline process-tree cases reached successful preparation,
native completion and their exact final assertions. Cumulative GUI active
work (milliseconds):

| Case | Copy | Persist | Prepare | Complete |
| --- | ---: | ---: | ---: | ---: |
| `writer-valid` | 3805 | 3851 | 5563 | **6429** |
| `writer-foreign` | 3770 | 3819 | 5501 | **6347** |

These completed measurements show why the old 5001–5002 ms abort observations
were not evidence that one or two extra milliseconds would suffice. Both now
complete below the approved ten-second GUI allowance. No parent/parser or
completion limit was raised, and no favorable-sample retry was performed.

Exact directories under `artifacts/payload-writers-20260926/`:

- `powershell-writer-valid-8f28af61f0094e9eac2839d4a6c2edf2`
- `powershell-writer-foreign-4ff5549acfa3499bb56f95c55f59289a`

Both GUI logs contain `attempt-payload-native-writer-handoff-ok`; their parent
results contain the corresponding exact `attempt-payload-native-writers-checked`
case marker. The negative case reached its intended foreign-origin assertion,
rather than failing before it. All four original sent/received files were
independently reopened with exclusive read/write access after the test process
exited; each is exactly 268435456 bytes. The fixture also checked release itself.
There are **104 distinct passing checks** including these two full-size cases.

## Boundaries and recovery

These are offline DevLocal/process-tree checks, not live GUI or NKN acceptance.
No full matrix, portable rebuild, live transfer, commit or push is performed.
The second coordinator-published `file_tuna_v4` requirement and all route/version
invariants remain unchanged. Historical Windows failures remain failures;
neither file length nor cleanup substitutes for the required native completion
and exact foreign-origin assertion. No further budget increase is authorized.

The recovery inventory and exact result/source/artifact hashes are sealed in
`artifacts/gui-work-budget-20260927/FINAL-VERIFICATION.json`. Only three production
files and the directly related test fixtures changed in this step; unrelated
pre-existing dirty work is preserved. No test host remains running.

The next live-validation step still requires its normal readiness review,
focused gate, fresh portable rebuild and unsandboxed preflight. This offline
milestone does not replace those gates or authorize a full matrix by itself.
