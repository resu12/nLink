# Evaluation-visit limit increase and native validation — 2026-09-29

## Outcome

The user-approved evaluation-visit increase is implemented. The resolver now completes the previously blocked witness selection, but the complete offline native milestone is **not passing**: the next failure is in post-resolution witness verification before the same-pair join.

No route acceptance was relaxed, no event-name lookup optimization was implemented, and no further limit was raised. There was exactly one native attempt, no automatic retry, no live NKN run, no full GUI matrix, and no commit or push.

## Exact contract and implementation

- Independent `EvaluationVisits`: **4,194,304**, previously shared with the 2,097,152 producer-record limit.
- `ProducerRecords`: **2,097,152**, unchanged.
- Cumulative metered work: **20,000 ms**, unchanged.
- Input records: **262,144**, unchanged. Other input/text/file/metadata limits are unchanged.
- The existing cumulative meter remains authoritative; no counter reset, exempt scan, skipped currentness check, or ownership/publication change.

The two production edits add the independent constant in `tools/FileTransferOps/LocalNativeInputCapture.cs` and retarget the existing comparison in `LocalFrontierPrimaryEvaluations.cs`. Six directly affected PowerShell controls now exercise the new boundary. There are eight changed source/test files in total.

One affected valid-evaluation fixture initially failed because it required physical origins `2,74,75`. Its genuine source contained an extra diagnostic row, producing `2,75,76`. The fixture now captures the original reader's exact physical mapping before handoff and compares the later evaluation against that mapping, retaining the non-copy/ascending checks. Production origin logic was not changed.

## Verification

- Boundary and canonical same-pair controls: **9/9 passed**.
- Affected evaluation/payload/witness/preparation lane: **27/28 initially passed**; only the fixed-origin fixture failed.
- Narrow valid/actual-evaluation follow-up on both PowerShell hosts: **4/4 passed**.
- Latest result for all **39 distinct selected checks: passed**, across **41 executions**. The original failed TRX and fixture remain preserved.
- Debug Core test-harness rebuild: **0 errors, 0 warnings**. Test DLL SHA-256 remains `cc53e201d12780014b4fcaf792fbdc51a4988ed3be8b074751ccd89cba59332c`.
- Source audit confirms only the new constant/comment and meter comparison changed in the two production files. All six edited PowerShell files parse; diff whitespace checks pass.
- Exact old-cap crossing, new-cap inclusivity, cap-plus-one rejection, cumulative maximum-state revalidation, and the unchanged 20,000/20,001 ms boundary are covered.

The first newly added preflight limit checker encountered `incompatible_local_ownership_types` after sharing a process with the loaded application assembly. No native attempt started. The checker was isolated in a fresh PowerShell process; the full preflight then passed, and the run repeated the full preflight successfully before launch. Product code was unchanged by this runner correction.

## Single offline native attempt

Fixture: `artifacts/canonical-tail-native-characterization-20260929/pwsh-d9f1febcf872477e81651ff82ca93142`.

- Native test: **failed**, duration **5m 46.338s**; analyzer exited 1 without timing out.
- Both peers completed **268,435,456 bytes** on `post_tuna_fallback_v6`, with the same session/transfer and SHA-256 `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
- The three-copy publication hold closed within its existing bound; the deadline did not move.
- Original analysis used **3 evaluations, 1 native read**, with unchanged 73,955-line input.
- Full/compact authoritative snapshot evidence and parity passed, with **1,507 boundaries** each.
- Witness resolver: **accepted**, one call, **27 witnesses**, **3,082,462 visits**, **19,223.4281 ms** cumulative work.
- Resolver-exit headroom: **1,111,842 visits** and **776.5719 ms**. These counters describe resolver exit, not the subsequent failure.
- All nine callback phases completed. Input-row projection processed 73,955 rows in 3,594.9191 ms.
- All ten original reports remained byte-identical. The original `FAIL_PROTOCOL_OR_INTEGRITY` / failed route gate was preserved.
- `SamePairJoined=false`; `OriginalOutputCommitted=false`. Diagnostic success is not route acceptance.

Thus the old 2,097,152-visit blocker is cleared for witness resolution. The new first observed failure is `AfterResolve`, at `FrontierLatePublicationPrimaryProbe.ps1:192`: “Witness lost original exact physical source line.”

## New edge: bounded diagnosis, not an unproven fix

That error covers a compound check: native batch ownership, unique/full index, original text, hash, and original physical origin. The existing export does not capture the failing subcondition, witness row, or final work/visit counters before cleanup. It does **not** establish source corruption or a physical-origin mapping defect.

Code inspection shows that `GetWitnesses`, each `Owns`, and each `GetPhysicalOrigin` call repeat `CheckCurrent`. That checks every full/compact line plus the bound diagnostic state. The post-resolution loop therefore has substantial additional cumulative demand; 4,194,304 is not demonstrated sufficient for the entire consumer path. No guard was removed or bypassed.

Next step: add narrowly scoped, test-only first-failure diagnostics around this post-resolution verification, preserving call order, checks, metering, and retirement. Record the failing operation/row and cumulative counters before failure cleanup. Run only the directly affected controls before one separately planned targeted native validation. Decide any further resource revision from that evidence; do not infer that increasing this resolver cap completed the milestone.

## Cleanup and preserved state

Read-only cleanup verification found no attempt peer/analyzer/test processes, no open native pins, and cleared diagnostic text. Both payload files were exclusively readable, exactly 268,435,456 bytes, and independently hash-verified.

HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`. All unrelated prior dirty files and evidence are preserved. The source/test changes affect two already-dirty controls plus six previously clean tracked files; this report adds one new untracked file. No staging, commit, push, process termination, or artifact deletion was performed.

Evidence roots:

- `artifacts/canonical-evaluation-visit-limit-20260929`: before copies, contract, source audit, focused test logs/TRXs.
- `artifacts/canonical-evaluation-visit-native-20260929`: build, frozen inputs, preflights, single-attempt marker, native TRX, observations, cleanup, final preservation audit and seal.

