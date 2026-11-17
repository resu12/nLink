# Witness-verification diagnostics — 2026-09-29

## Checkpoint

Test-only first-failure instrumentation is implemented, including a private-scope correction found during the single offline native attempt. **The complete native milestone still fails, and the corrected instrumentation has not yet had a native rerun.** No production code, correctness rule, or resource limit changed in this phase.

The latest result for all **54 distinct selected checks is green** (73 executions: 19 focused, 35 affected composition/state/cleanup checks, and 19 focused follow-ups). The fixed helper must next be validated in one fresh targeted offline native attempt; do not reuse the already-consumed runner.

## Changes and safeguards

- Added `FrontierWitnessVerificationDiagnostics.ps1` under test support.
- Wrapped the existing witness predicates in `FrontierLatePublicationPrimaryProbe.ps1` without reordering, duplicating, removing, or weakening them.
- Added explicit condition labels for capacity, ownership, index uniqueness/bounds, full/compact text, hash, and physical origins.
- Capture scalar pre-check row identifiers and cumulative counters; freeze the first rejection, exception, or open-to-closed transition. The latter matters because original retirement can clear a row before a comparison reads it.
- Diagnostics never call currentness, start/stop/reset a meter, reopen a source, issue authority, retain witness text, or replace an original predicate result/exception.
- Expanded `FrontierLatePublicationGuardTests.cs` to cover exact failed-condition identification, successful call order, short circuit, first-closure freezing, original exception propagation, broken/missing instrumentation, and private-scope reference identity on both PowerShell hosts.

Only those three test files changed. The test DLL was rebuilt; application/Core/Infra production sources and binaries remain unchanged.

## Single native attempt

Frozen inputs: `artifacts/canonical-witness-verification-native-20260929/RUN-INPUTS.json`, SHA-256 `875e2684f3c39969d92650db9933111d53943d5b99f65d7b8d9a4fc3d9f645bc`.

Fixture: `artifacts/canonical-tail-native-characterization-20260929/pwsh-9539c7fc22ae48f19dcb98943e3b2cb1`.

- Full preflight passed twice, outside the restricted sandbox.
- Exactly one offline native attempt; no live NKN, portable GUI, full matrix, or automatic retry.
- Native test **failed** after **5m 46.5355s**.
- Both peers completed **268,435,456 bytes**, same session/transfer, matching SHA-256 `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
- The three-copy publication hold closed within its existing bound.
- Resolver accepted **27 witnesses**, with **3,082,253 visits** and **19,032.7374 ms** cumulative work.
- The first recorded post-resolution rejection was **ownership**, for the pre-check row `FullIndex=44`, `CompactIndex=16`, `SourceLine=122`.
- The diagnostic's before/after counters and closure fields were null. This limits the conclusion: the exact budget/retirement cause is **not established by this trace**.
- `SamePairJoined=false`, `OriginalOutputCommitted=false`. The original failed protocol/integrity verdict and failed route gate remain unchanged.
- All ten original reports remained byte-identical.
- Cleanup: no attempt processes remain; native pins are closed and text cleared. Both payload files were exclusively readable, the expected size, and independently hash-verified.

Witness resolution is not whole-path acceptance. This phase does not establish source corruption or a physical-origin defect.

## Instrumentation RCA and correction

The original analyzer declares `nativePrimaryCustody`, `nativePrimaryFull`, `nativePrimaryCompact`, and `nativePrimaryPair` as PowerShell `private` variables. The new nested helper attempted to read those values implicitly. PowerShell private scope hides them from that function, so best-effort diagnostic reads failed and left counters null. The nested predicates also needed explicit access to those original private references.

The executed helper/source/DLL versions were preserved under `executed-inputs` before correction. A private-scope regression against that old helper fails with “The variable '$nativePrimaryCustody' cannot be retrieved because it has not been set.”

Correction: pass the four original references explicitly into helper parameters. They remain invocation-local, not script/global retained authority. The tests now mark the probe inputs private and require exact reference identity in the nested predicate. The focused **19/19** follow-up passes, including both hosts; no unrelated lane was rerun.

The corrective build had **0 errors** and **343 existing warnings outside the edited test file**. The pre-native incremental build had 0 warnings/errors. Final test DLL SHA-256: `6eda39e46329aab9a64915b98807cf55ef627826e2bf42d043fa3b7418eedc27`.

## Next step

1. Rebuild the current test harness, freeze the corrected files and DLL, and require the same strict preflights.
2. Run one fresh targeted offline native attempt with the corrected helper. Verify that before/after counters and closure fields are populated, and preserve the first failing operation.
3. Only then choose a bounded fix or resource-contract revision. Keep evaluation visits **4,194,304**, producer records **2,097,152**, cumulative work **20,000 ms**, all input limits, authority checks, and route acceptance unchanged until that decision.

Code inspection shows substantial post-resolution demand: the resolver itself calls `GetWitnesses` and `IsCurrent`, then the probe calls `GetWitnesses` and per-row ownership/origin checks. Each repeats whole-state validation. This is a reason to account for the full consumer path, not proof of which guard fired first in this attempt.

HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`. No commit, push, artifact deletion, or process termination occurred. Final preservation evidence is in the native artifact root; focused results and before copies are in `artifacts/canonical-witness-verification-diagnostics-20260929`.

