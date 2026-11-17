# Native diagnostic custody and primary-witness integration review

Date: 2026-09-28  
Branch: `v0.7.0`; HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Disposition: review complete; bounded test-harness integration proposed, not implemented or authorized by this review.

## Verified state and first missing edge

The preceding small-profile characterization passed: 172,032 transferred bytes, matching SHA-256, original native `PublishedBeforeOriginalDeadline` observation, one 6,537-byte diagnostic payload marker, and successful original app-to-GUI writer adoption. See `file-transfer-broader-offline-fault-implementation-20260928.md` for the original evidence and focused tests.

This review verified all 1,265 prior pins and all 471 dirty-file entries without changes. No test, build, analyzer invocation, transfer, commit or push was performed.

The first missing integration edge is **GUI finalizer processing and native parent handoff of that diagnostic payload catalog**. The small profile currently processes a drain-only semantic output, retires the original join during source cleanup, and reaches a retained-analysis placeholder rather than the original primary analyzer. Successful app-to-GUI adoption does not prove GUI-to-wrapper-to-RouteAcceptance-parent-to-primary custody.

The preceding report's phrase “no-supported-failure outcome” is an expectation, not an observed primary result. Primary analysis has not run on this profile. A positive producer outcome must not be substituted for its verdict.

## Two distinct payload contracts

| Contract | What it owns | Suitability for this profile |
| --- | --- | --- |
| Normal native diagnostic custody | Original writer ledger/output pins and frontier receipt payloads, followed by the original primary evaluation pair | Reuse this existing path. |
| Test-only actual transfer-file pair | Original source and destination file custody; current primary probe requires 268,435,456 bytes and the fixed canonical SHA | Do not reuse or weaken it for a 172,032-byte in-memory-source transfer. |

The fixture's held 256 MiB source is not the small transfer's `MemoryStream`. Binding that unrelated file to the small destination would misrepresent the transfer even if writer IDs matched. No source/destination-pair or live-256 claim is warranted.

The normal native preparation already appends receipt pins from each adopted writer's original manifest after its ten fixed inputs. Its GUI rendezvous, completion, wrapper forwarding, parent relay adoption and primary payload capture do not require `TryOfferPayloadsToWrapperForTest` or the actual transfer-file pair extension.

## Recommended bounded implementation

Add one separately named, opt-in **diagnostic-custody-only small-profile integration test**. Reuse the approved eight-chunk schedule unchanged; keep existing profiles and canonical-pair assertions unchanged.

1. **GUI finalizer:** use `TryProcessObservedLedger(Get-GuiSmokeSemanticEvidenceEventNames())`, preserving original writer/cursor/recovery matches. While the original binding is still current, copy and persist semantic evidence, then perform the existing parent preparation/completion. Place this before source-join cleanup can retire the shared owner. Keep the receipt's original native pins; do not reopen artifacts, import JSON as custody, copy capabilities or revive disposed joins.
2. **Wrapper and RouteAcceptance parent:** use their ordinary native rendezvous and `TakeRelayAfterExit()/TryTakeFrontierCustody()` flow. Pass the actual `LocalWriterContext` and `FrontierRelayCustody` supplied at the existing retained-analysis call site. Keep actual-pair wrapper/parent test hooks off. Do not substitute the pair adapter, a fabricated writer, or reconstructed custody.
3. **Primary:** invoke the original retained-analysis worker once. Observe its already-created `nativePrimaryPayloads` after the original full/compact evaluations and single capture. Validate the one original payload's writer/observation, byte count/hash, schema/outcome and matching native marker/completion origins. Keep existing source-origin and cleanup checks. Do not call payload capture twice, add an evaluator/correlator, or select a failure in a probe.
4. **Primary-selected witnesses only:** preserve the existing diagnostic branch and its original first compact proof-chain failure. When that failure is unsupported or absent, no witness resolver or witness-backed diagnostic publication may be invoked. A successful positive catalog capture with no eligible witness is a custody milestone only. If the primary actually selects the supported unresolved failure, preserve it and stop for review of that first discrepancy; do not turn it into the anticipated no-failure case.
5. **Result and cleanup:** save the original report, receipt and first failure unchanged, and stop explicitly before route-cycle acceptance. Keep the original source-read/evaluation counts and watchdogs; verify child exit, custody retirement, released handles, restored environment and unchanged original artifacts.

No production change is expected. If normal custody cannot be exercised without changing runtime/diagnostic APIs, expanding budgets, changing ownership or weakening an assertion, stop at that first edge and review it separately.

## Witness eligibility is conditional, not relaxed

`LocalNativeFrontierWitness.ps1` accepts the positive producer schema as well as `PublishedWithinObservationBound`, but requires the original primary-selected `filetransfer_fallback_frontier_recent_progress_grace_recheck_missing` failure, one matching unrecovered candidate and unresolved recent-grace proof. `AnalyzerOrchestration.ps1` invokes it only for that original first failure.

The existing 1–64 exact source-line requirement remains intact **whenever witness resolution is invoked**. Skipping an ineligible branch is not permitting an empty witness. Never invoke the one-shot resolver merely to probe an absent failure: rejection closes its diagnostic inputs. A receipt catalog does not itself confer route, runtime, retry, publication or acceptance authority.

## Verification and authorization boundary

After separate approval of the bounded implementation and one fresh small offline integration:

- Run directly affected deterministic harness/custody tests; retain existing negative ownership, original evaluation and canonical-pair controls. Do not rerun unrelated aggregate lanes.
- Rebuild the Debug native test harness, then run exactly one fresh original-pair 168 KiB diagnostic integration with explicit escalation. The old run's closed custody cannot be reused.
- Require original schedule/SHA success, complete native handoffs, exactly one primary launch and native source read, the existing worker's three evaluator calls, original catalog/hash/origin checks, truthful primary witness gating and complete cleanup.
- Treat an unchanged incomplete route proof as an incomplete route proof, not a passing live contract. A failure at any custody edge stops the integration; do not fall back to path-based analysis and label it native proof.

No live-256, portable-package GUI validation, full matrix, acceptance relaxation, production timing/queue change, commit or push is included. Native diagnostic integration remains unverified until that future run.

## Source anchors

- `tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersJoin.ps1`: complete-native-writers selection and drain-only/default processing.
- `tests/nLink.SmokeTests.Core/Support/FrontierAttemptSourceJoin.ps1`: two existing destination-join cases and shared-owner retirement.
- `tests/nLink.SmokeTests.Core/Support/FrontierAttemptDestinationJoin.ps1`: existing copy/persist/prepare/complete sequence, currently reached through the canonical pair fixture.
- `tools/FileTransferOps/LocalGuiWriterParentPreparation.cs`, `LocalGuiWriterParentRendezvous.cs`, `LocalGuiWriterParentCompletion.cs`: independent native receipt pins and original handoff.
- `tools/Run-FileTransferTunaGuiSmoke.ps1`: original post-exit completion/relay forwarding.
- `tools/Run-FileTransferRouteAcceptance.ps1`: native relay custody take, retained-analysis arguments and source preparation.
- `tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1`, `FrontierRelayAnalysis.ps1`, `FrontierRetainedParentWorker.ps1`: current placeholder, original worker integration and single-read/evaluation-count controls.
- `tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryProbe.ps1`: distinct canonical actual-file-pair contract.
- `tools/FileTransferOps/AnalyzerOrchestration.ps1`, `LocalFrontierPrimaryPayloads.cs`, `LocalNativeFrontierWitness.ps1`, `LocalFrontierPrimaryWitnesses.cs`: original evaluation pair, native payload capture and conditional primary-owned witness selection.

Review evidence and preservation seal: `artifacts/native-diagnostic-custody-review-20260928/`.

