# Evaluation-only resource implementation and native result — 2026-09-29

## Status

Implemented the reviewed evaluation allowance and verified 68 focused/affected controls. The one fresh offline native attempt advanced past the previous witness-verification visit-cap failure, but **failed at the canonical same-pair join**. Original diagnostic output was not committed. This is not route-cycle acceptance, and no live NKN/full GUI run, automatic retry, commit, or push was performed.

Branch: `v0.7.0`; HEAD unchanged: `a653d4063070235c71fe211480aeb37863d9b2a4`.

Entry checkpoint: `artifacts/canonical-full-consumer-budget-review-20260929/FINAL-VERIFICATION.json`, SHA-256 `bcb950001c63a23d6f681ac1d8839def8f1015e33ce18cea69965bae35b50b6e`. Entry check verified 6,981 pins and 613 dirty entries. Earlier work was preserved.

## Implemented scope

- `EvaluationVisits = 1,073,741,824`; new `EvaluationMilliseconds = 60,000`.
- Evaluation custody uses the original cumulative work stopwatch. No reset/refill, added evaluation, source reread, or public limit parameter.
- `VisitEvaluation` retains 20 seconds during `BeginEvaluation`; 60 seconds is available only after successful assignment of original evaluation custody.
- A private resource predicate shares the original closed/base/scope/files/pins short-circuit checks. Default `ResourceCurrent` remains at 20 seconds; only the evaluation currentness caller supplies 60 seconds.
- Source admission/read/origin/completion, startup, idle rendezvous, producer records, input capacity, metadata/text limits, authority and acceptance are unchanged.
- The test-profile resource observer reports the selected limit; its source-read default remains 20 seconds.
- Evaluation-expiry fault injections now use evaluation limit + 1 second. Producer/source fault injections remain unchanged.
- The maximum-capacity arithmetic test verifies actual per-pass cost, then places an inert meter near the visit boundary instead of executing a billion visits.
- The default worker projection hash was updated only after reviewing the small production diff; the source-reader projection hash is unchanged.

Changes are confined to 13 source/test files (three production files) plus the rebuilt Core test DLL. Before copies are under `artifacts/canonical-evaluation-resource-implementation-20260929/before`.

## Verification

| Gate | Result |
| --- | --- |
| New 21-second regression before production change | Expected failure on both PowerShell hosts |
| Core test build | Passed; 343 existing warnings, zero errors |
| Focused boundary/phase/source-profile lane | 19 passed |
| One directly affected contract lane | 49 passed |
| Fresh pre-native incremental build | Passed; zero warnings/errors |
| Strict preflight-only and execution preflight | Passed; 7,767 frozen pins, 616 dirty files, no conflicting processes |
| Single offline 256 MiB native test | Failed after 5m23.544s; no timeout |
| Post-run process/payload cleanup | Passed |

Focused controls cover exact visit cap and plus-one, 20-second producer boundary, 21-second valid evaluation, exact 60-second/plus-one arithmetic, source operations not acquiring evaluation allowance, copied/repeated calls not granting ownership or refilling counters, and expired custody not reopening. The affected lane covers witness bounds/identity, preparation/output, cancellation, rendezvous and source read/origin limits on the applicable hosts. Synthetic arithmetic controls do not claim native ownership.

Current test evidence:
- `artifacts/canonical-evaluation-resource-implementation-20260929/test-results/red-extended.trx`
- `artifacts/canonical-evaluation-resource-implementation-20260929/test-results/focused.trx`
- `artifacts/canonical-evaluation-resource-implementation-20260929/test-results/affected-contract.trx`

Frozen native inputs: `artifacts/canonical-evaluation-resource-native-20260929/RUN-INPUTS.json`, SHA-256 `c5e9417fa05b132ce43546aba45a1aae07f05be55ce0b3c54da8d8a935359d64`. The isolated preflight confirmed evaluation 60,000 ms / 2^30 visits and producer 20,000 ms / 2,097,152 records. Historical regression records in the preflight are preserved evidence, not newly rerun tests.

## Fresh native evidence

Fixture: `artifacts/canonical-tail-native-characterization-20260929/pwsh-ea02c04b81924475b7f7b5f50fdaa48e`.

- Both original peers completed 268,435,456 bytes on `post_tuna_fallback_v6`, SHA-256 `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
- Three held physical copies closed within the existing four-second bound. The original recheck deadline was unchanged; its measured remaining margin was about 72.9 ms. This is an observation, not a relaxed acceptance rule.
- Original analyzer: three evaluations, one native source read. Input stayed unchanged at 73,876 events.
- Full and compact evidence remained complete/authoritative, each with 1,507 boundaries; both artifact-evidence verdicts and parity passed.
- Resolver accepted exactly 27 witnesses in one invocation at 3,079,374 cumulative visits and 18,308.7735 ms work.
- The subsequent complete witness loop passed ownership, unique index, text, hash, full/compact origin, selected-failure identity and native-range checks. This follows from reaching the canonical join call after `latePublicationResolved=true`; the earlier failure was inside this loop.
- `TryCheckCanonicalSamePairWitnessForTest` then returned false. `SamePairJoined=false`, `OriginalOutputCommitted=false`.
- Original verdict remains `FAIL_PROTOCOL_OR_INTEGRITY`; selected failure remains `filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.
- All ten original report hashes match before/after and disk. Eleven diagnostic pins closed; retained text cleared. Both payload files are exclusively readable with their expected hashes. No known attempt PID or conflicting process survived.

Native result/derived checks: `characterization.trx`, `observation.json`, and `cleanup.json` under `artifacts/canonical-evaluation-resource-native-20260929`.

## First remaining edge / RCA

**Confirmed:** the first failed proof edge is the test-only original-transfer-pair/canonical-receipt join, after complete witness verification. The run is not a success merely because the transfer hashes, evidence parity, or witness loop passed.

**Static inconsistency found:** `FrontierAttemptPayloadPrimary.cs:260` implements `TransferPairCurrentForTest` using default `ResourceCurrent`, which intentionally remains a 20-second producer predicate. That helper is consumed by evaluation-owned binding/currentness and by the canonical same-pair join. Consequently those test-only consumers can still reject after 20 seconds even when original evaluation custody is valid within 60 seconds. The previous full-consumer review counted these consumers' work but missed this separate test-profile time predicate.

**Not yet proven for this run:** the exact short-circuit predicate that rejected the join. The helper collapses resource currentness, held-pair/channel currentness and receipt scope into a false result without recording a first-rejection snapshot. The 18.309-second value is the resolver-acceptance snapshot, not the join-entry time. Do not present a later elapsed value or precise failing predicate as measured, and do not raise limits again based on this inference.

## Bounded next step / fix plan

1. Reproduce the phase mismatch in a focused original-pair fixture at 21 seconds: evaluation custody should remain valid while the existing pair guard rejects. Cover the 60-second expiry, copies/replays, cancellation, channel state and receipt-scope negatives. If native first-predicate evidence is required, record the existing short-circuit result/counter/clock once; never invoke currentness again merely for diagnostics.
2. If confirmed, make only the original evaluation-owned test-pair guard use the evaluation resource allowance, preserving all held-file/channel/identity checks and before/after checks. Keep producer attachment and raw source APIs at 20 seconds. Do not weaken receipt-scope matching or add a fallback.
3. Run only the new regression and directly affected pair/join controls. Do not repeat the 68 unrelated-to-that-follow-up executions or a full aggregate matrix.
4. Rebuild and freeze changed inputs before a separately controlled single targeted offline run. Require same-pair join, original output commit, unchanged reports/verdict and cleanup. No automatic retry from this attempt.

The live GUI matrix remains deferred until the targeted diagnostic path is complete and the applicable route-acceptance gate is separately satisfied.

