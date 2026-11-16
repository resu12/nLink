# Live-256 readiness review

Date: 2026-09-28. Review only; no code, acceptance, build, test, analyzer,
transfer, commit or push changes.

## Decision

**Not ready for the next live-256 diagnostic integration run under the existing
reviewed contract.** Do not repeat the successful small positive capture, rerun
the unchanged canonical fixture, or start the full GUI matrix.

Two earlier blockers are now closed: the actual 256 MiB source/destination pair
reached original parent/primary analysis, and the small native diagnostic receipt
reached the original primary with verified cleanup. The older September 27
readiness report must not be used to describe those handoffs as still missing.

The next unfinished milestone is a **genuine primary-selected, failure-bearing
native witness on the same transfer**. Its feasibility must be established before
implementing another profile or scheduling another transfer. This is an
integration requirement from the reviewed contract, not a newly discovered
runtime defect or an added live acceptance rule.

## Current gates

| Gate | Current evidence | Disposition |
| --- | --- | --- |
| Actual 256 MiB pair and original analysis | The canonical-fallback run completed analysis, checked original 2/2/1 parent guards, three evaluations/one read, matching payload SHA, and cleanup. | Closed for that offline custody profile; not route-cycle acceptance. |
| Native diagnostic catalog to primary | Fresh 168 KiB run captured one native receipt with 11 pins, retained original full/compact views, and released every pin and payload text. | Closed for the small positive profile. |
| Original primary-selected failure and exact native witnesses | Fresh primary selected no proof-chain failure; resolver calls, resolution attempts and witness attempts were all zero. | Unverified; the positive receipt cannot satisfy it. |
| Failure accounting and live dispatch ownership | Selected-failure shadow still reserves the original one-shot writer; unique live scenario call supplies only Context. | Separate integration/enablement gate, not permission to consume or bypass that writer. |
| Fresh package and live preflight | Default portable executable is dated September 13; no package was rebuilt or network/bridge readiness checked here. | Required after integration gates, immediately before authorized targeted live validation. |

The 256 MiB and 168 KiB results are different runs and different payload contracts.
They must not be combined into a claim that a failure witness has been verified on
the held canonical pair. The small transfer uses its own MemoryStream source;
its writer receipt is not ownership of the fixture's unrelated 256 MiB source.

## Why another identical positive run is not the next step

The original analyzer invokes native witness resolution only for its own first
selected `filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`
failure (`tools/FileTransferOps/AnalyzerOrchestration.ps1:9185`). The resolver
also requires the matching unrecovered original candidate, unresolved proof,
original evaluation-owned views, same writer/transfer receipt, and 1-64 exact
native source lines (`LocalNativeFrontierWitness.ps1:54`).

The fresh small profile correctly did not enter that branch. Its test probe
explicitly stops for separate review if the supported failure is selected
(`tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticPrimaryProbe.ps1:97`).
Weakening that assertion or calling the resolver with a substitute failure would
not verify the missing integration.

The observed primary result remains FAIL_PROTOCOL_OR_INTEGRITY. Its absent GUI
summary leaves gui_integrity_ok=0; the fixture performs only the initial Tuna to
fallback transition and lacks the second Tuna activation. These are coverage
limits of this offline fixture, not evidence of a newly reproduced live GUI bug.
Payload SHA and native receipt success do not override either live requirement.

## Remaining milestone sequence

1. **Failure-bearing witness integration.** First review a separately named,
   default-off fault profile that can produce the supported unresolved failure
   while preserving a valid native producer receipt on the same original pair.
   Establish the actual admissible frame and publication schedule against the
   unchanged classifier and original clock limits before coding. Keep the current
   positive test as its negative-selection control. Do not inject a failure,
   edit clocks, seed authority, widen deadlines, borrow a receipt, or add another
   evaluator. If no viable schedule is established, report that precise contract
   boundary rather than retrying for luck. Small characterization and canonical
   same-pair integration remain distinct verification steps.
2. **Failure accounting and live ownership.** Verify the selected-failure path
   without consuming the one-shot writer before the original witness branch.
   Review the actual live dispatch/launch/finalization wiring and default loader;
   do not simply add FrontierOwner to a call and assume the offline test-only
   attempt/file-pair hooks are production-ready. Keep verdicts and acceptance
   unchanged. Run only directly affected controls after bounded implementation.
3. **Package and strict preflight, then the targeted live gate.** Rebuild portable
   from the frozen, verified worktree, record source/binary/bridge provenance,
   and obtain authorization for the targeted run. Use escalated
   Run-FileTransferRouteAcceptance.ps1, phase5-analyzer-gui-acceptance,
   StrictCampaignPreflight, and only
   regular-v4-live-activation-off-on-off-256mb. No fake/dry/skip path or continuation
   after a failed preflight. Require Regular4 -> Tuna4 -> fallback6 -> Tuna4 ->
   fallback6, including the second coordinator-published file_tuna_v4.
   Keep correctness separate from goodput. Full matrix follows only at its
   separately authorized gate.

These are three milestone groups, not a promise of three turns or three tests.
No new implementation or transfer is performed by this review. In particular,
the missing witness branch is not a reason to relax the existing live contract.

## Read-only verification and preservation

All 1,403 prior pins and all 479 prior dirty entries matched at entry. The GUI
script parses without errors; its only live handoff/fallback call remains
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx` at line 42981.
The normal loader includes native receipt/witness support but not the test-only
attempt/actual-file-pair extensions. TerminalPreVerdictObserver.ps1:44 is a
test-only reservation, not a production authority failure.

The default portable nLink.exe is 135,108,819 bytes, last written
2026-09-13T18:02:18.3413197Z, SHA-256
b012cdf4b4d9b620c1de459679a3551e4d704023d4e399ff907b9090fea84ae7.
A separately retained September 23 package records commit f02f6d04, also not
current HEAD a653d406. Neither establishes current dirty-worktree provenance.
No claim of live network readiness is made.

No tested code changed, so no tests or aggregate lanes were rerun. This report
and ignored review/seal artifacts are the only additions. Earlier source,
binaries and successful/failed captures remain untouched. Exact evidence and
the final independent preservation check are in
artifacts/live-readiness-review-20260928/.
