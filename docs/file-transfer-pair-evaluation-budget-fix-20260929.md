# Test-only pair evaluation budget fix — 2026-09-29

## Outcome

**The canonical offline diagnostic gate now passes.** This step passed seven focused regressions, fourteen directly affected controls, and one fresh offline 256 MiB canonical-tail test. No live NKN test, full GUI matrix, automatic native retry, commit, or push occurred.

The offline profile deliberately preserves `FAIL_PROTOCOL_OR_INTEGRITY` / `fail` for its supported unresolved route failure. Its success means the original diagnostic witness, same-pair join, output and cleanup contract completed; it is **not** live route-cycle acceptance.

Branch `v0.7.0`, HEAD unchanged: `a653d4063070235c71fe211480aeb37863d9b2a4`.

## RCA and minimal correction

The preceding fresh native run completed witness verification but rejected the canonical same-pair join. Inspection found that the private test-only `TransferPairCurrentForTest` still called the 20-second producer `ResourceCurrent` predicate, although its callers had already established original evaluation ownership under the approved 60-second allowance.

The new real-pair regression first proved original evaluation custody current at 21 seconds, then attempted pair binding. Before the fix:

- Windows PowerShell rejected at 21,001 ms: `resource:budget`.
- PowerShell 7 rejected at 21,000 ms: `resource:budget`.
- Both failures occurred after real original parent/worker/file handoffs. No synthetic object granted ownership.
- These are focused reproductions of the mismatch; the previous full native run did not record its exact join-rejection predicate or join-entry clock.

The behavioral fix is **one line** in `tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimary.cs`: evaluation-owned pair currentness uses `ResourceCurrentWithin(LocalPublicationInputLimits.EvaluationMilliseconds)`.

All four call sites remain behind original evaluation checks. The held-file identity/currentness, channel existence/currentness/state, receipt-scope matching and before/after canonical join checks are unchanged. Producer pair attachment retains default `ResourceCurrent`. Source admission/read/origin/completion, startup/rendezvous, all capacities and route authority/acceptance are unchanged. No production runtime source was edited in this step.

The other edits are regression controls in `FrontierPayloadPrimaryTests.cs` and `FrontierAttemptPayloadPrimaryProbe.ps1`. They cover valid original binding/currentness at 21 seconds, rejection at 61 seconds, channel retirement, copied/repeated calls, unchanged cumulative clock/counters and false authority flags. Existing tests cover foreign completion, retired files/owner, canonical receipt-scope/ownership negatives and default-loader exclusion.

## Verification

| Gate | Result |
| --- | --- |
| Pre-fix original-pair regression, both hosts | Two expected failures; exact budget cause recorded |
| Focused phase/expiry/channel and producer-isolation checks | 7 passed |
| Directly affected pair/join/default-loader lane, once | 14 passed |
| Fresh pre-native Core harness build | Passed, zero errors/warnings |
| Strict preflight-only and execution preflight | Passed; 8,419 frozen pins, 617 dirty entries, no conflicts |
| One offline canonical-tail test | Passed in 6m05.448s |
| Independent process/file cleanup | Passed |

The earlier full rebuilds had 343 existing warnings and zero errors. The previous phase's 68-test lane was preserved as historical evidence, not rerun. Nested synthetic scope checks are not counted as extra xUnit tests.

Implementation evidence: `artifacts/canonical-pair-evaluation-budget-fix-20260929/`, including `test-results/red.trx`, `focused.trx`, `affected-contract.trx`, and `red-reproduction.json`.

## Fresh native evidence

Fixture: `artifacts/canonical-tail-native-characterization-20260929/pwsh-f8cb2c03802543a5bd5d00d7e1383df6`.

- Both original peers transferred 268,435,456 bytes on `post_tuna_fallback_v6`.
- Both SHA-256 values: `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
- All three held copies closed within the unchanged bound.
- Original parent checks remained intact; original analyzer ran three evaluations and one native read. Its 73,950-event input remained unchanged.
- Both full/compact evidence views remained authoritative and complete, with 1,507 boundaries each; artifact-evidence verdicts and parity passed.
- The resolver accepted 28 witnesses in one invocation at 3,082,276 visits / 19,575.126 ms cumulative work. This is the **resolver-acceptance snapshot, not the final output-commit clock**.
- Full witness checks completed; `SamePairJoined=true`; `OriginalOutputCommitted=true`.
- Original verdict stayed `FAIL_PROTOCOL_OR_INTEGRITY`; selected failure stayed `filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`. No diagnostic observation was promoted into route authority.
- All ten original report hashes match before/after and on disk.
- No diagnostic pins or retained text survived; both payload files are exclusively readable with the expected lengths/hashes. No known attempt PID or conflicting native/test process survived.

Native evidence: `artifacts/canonical-pair-evaluation-budget-native-20260929/characterization.trx`, `observation.json`, and `cleanup.json`. The evidence-only cleanup checker initially had a generated-script syntax error; it was repaired and parsed before the successful independent check. That required no code change or native retry.

Frozen inputs SHA-256: `34b541d3f92eb5f3c7fda2d9baff670e7c9d166be800969f113781c5da4462ff`. Executed test DLL SHA-256: `bbeabdcafe5859e5911269e10a23e6f442c4c3dd857bbf4b52897223016466d1`.

## Preservation and next gate

Entry verified all 7,834 prior pins and 617 dirty entries against `artifacts/canonical-evaluation-resource-native-20260929/FINAL-VERIFICATION.json` (SHA-256 `6a6267b0e8c2debead5b20855b2c798284b8c7aecb1f036c809f891bfb10ab17`). Before-images preserve the three edited test files and old test DLL. Prior production files, binaries, reports and failed captures remain intact; the only additional repository artifact is this report.

Next: review this completed offline gate and the exact candidate for targeted live-256 readiness. A live run requires its own agreed scope, a fresh portable rebuild and strict escalated preflights. The full GUI matrix remains a later gate. Do not infer a successful route cycle from this offline diagnostic pass or from fallback completion/SHA alone; the live activation cycle still requires the second coordinator-published `file_tuna_v4` route.

