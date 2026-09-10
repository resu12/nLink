# Bounded plan: non-authority data-path diagnostics beside authoritative GUI results

Status (2026-09-10): approved, implemented and reviewed. The original joined
gate was 19/20; its pre-existing stale assertion is now reviewed and corrected,
with a focused 3/3 follow-up passing. The joined gate was not repeated.
See [implementation results](file-transfer-authoritative-gui-diagnostic-companion-results.md).
The proposal below is retained as the original scope record.

## Recommendation and baseline

Start from `9bde1bae89f722858ef6af10c5a05f7700aceb4a`, the reviewed shared-matcher
and isolated-shadow commit. Its joined gate passed 17/17. No tests or reports were
rerun while preparing this plan.

Add a diagnostic companion to the existing immutable GUI failure-enrichment
workflow. Preserve the primary classification exactly; do not replace the legacy
GUI reducer or use the shared matcher's recovery result as GUI/Coordinator authority.
This is a narrow integration phase, not full GUI/retained policy cutover.

Proposed bound: three cumulative active engineering hours across agents, focused
dual-host tests and one final joined contract gate. Implementation needs separate
approval. No aggregate suites, historical end-to-end reports, package rebuild,
preflight, live attempt or allowance restart.

## Source findings and placement

`Get-TunaGuiFileTransferSetupFailureClassification` returns early for malformed
Snapshot evidence, Coordinator boundaries and authoritative pass/harness outcomes.
Those returns must remain in place. Its result currently has no reliable explicit
Snapshot-mode/verdict carrier; a `ClassificationSource` prefix is not sufficient
to establish eligibility.

The request worker calls that classifier and then writes its response. The parent
rejects a timeout or nonzero child exit before adopting the response. Consequently,
putting diagnostic work into the same timed worker can discard an otherwise valid
primary classification, even if its JSON was written first.

The safe insertion point is after the caller has adopted the primary result and
atomically written completed `filetransfer-tuna-gui-error.json`, currently around
`GuiSmoke-Windows.ps1:37867–37877`. Complete or cancel the subordinate diagnostic
work before the existing artifact freeze. No orphan process or late artifact write.

This insertion covers the existing **failure-enrichment path only**. It does not
add classification work to successful GUI scenarios. A successful GUI run can
still fail retained data-path analysis; the retained report remains responsible
for that case. Do not claim this phase alone exposes every operator finding on
the GUI-success path or changes Phase-5 acceptance.

## Contract decisions

### Primary versus companion

| Existing evaluated outcome | Companion behavior | Primary result |
|---|---|---|
| Snapshot evidence invalid/incomplete, or parity failure | Skip; report explicit ineligible status | Unchanged evidence failure |
| Valid Snapshot stream with a selected Coordinator boundary | Skip in this bounded first-edge phase | Unchanged Coordinator boundary |
| Valid authoritative stream, no selected authority boundary, completed immutable failure enrichment | Evaluate if original budget remains | Unchanged harness classification, including fail-closed harness fallback |
| Missing/malformed eligibility carrier, LegacyV0 or unfinished enrichment | Not evaluated; no invented pass | Unchanged |
| Diagnostic error, timeout, changed input or malformed response | Failed/timed-out/not-evaluated companion only | Unchanged already-persisted primary |

No companion result may clear a failure, mark a transfer recovered, change GUI
exit status, select a retained attempt, grant authority, or create a finalization
receipt. Empty or unavailable diagnostics are not proof of correctness.

### Candidate/count domain and recovery labels

Use the committed adapter's canonical receive-record domain: identical original
receive records are coalesced only after excluding appended location metadata;
all evidence, including conflicting records, remains in the shared index.

Report `candidateCount`, `sharedMatcherRecoveredCount` and
`unresolvedCandidateCount`, not Coordinator quartets or the retained scalar.
Name each nested result's recovery field `sharedMatcherRecovered` when presenting
it outside the test adapter, preserving its Boolean value and the existing reason,
variant and receive disposition. It means only that the unchanged retained matcher
satisfied its evidence rule. It must never be displayed as “GUI recovered.”

The existing GUI uniqueness, source-before-transition, current first-activation,
conflict, terminal, replacement and ABA vetoes remain unchanged. Their known
differences from the shared matcher remain characterized. Do not introduce another
matcher, silently relax these checks, or broaden the existing Tuna receipt window
into a claim of exact-current-publication provenance.

## Implementation order

1. **Freeze adoption and failure isolation first.** Add tests showing that a
   completed primary classification and its serialized primary fields remain
   byte-identical with a passing, unresolved, failed, timed-out or absent companion.
   Test actual child-process ordering/cleanup, not only a mocked return object.

2. **Add a minimal read-only eligibility carrier.** Populate it from the existing
   Snapshot evaluation, without reevaluating snapshots or changing their normalized
   result. Carry explicit mode, evidence verdict, completeness/parity outcome and
   whether a primary authority boundary exists, together with the immutable input
   identity already available to the request. Missing or invalid carrier disables
   only the companion; it cannot invalidate a valid primary response. Do not infer
   eligibility from class names, writer chronology or `ClassificationSource` text.

3. **Invoke an isolated companion after primary persistence.** Reuse
   `GuiRejectedIngressShadow.ps1` in a separate short-lived process/bundle, never
   dot-source its parser-resetting initialization into an active analyzer. Read
   only the same immutable input paths and verify their content identity; retain
   original cross-file locations and `gui_evidence_sequence`, never compressed
   ordinals. Reuse existing SHA helpers rather than inventing hash/discovery code.
   Parse once, select the same nine event types, build one shared index, and call
   the existing matcher. No broad reporting imports or new authority correlation.

4. **Keep the original deadline.** Capture the primary enrichment's monotonic
   deadline once. The companion can consume only what remains after primary
   adoption/persistence, further bounded by the existing finalization deadline and
   safety reserve. Do not call the budget helper again to obtain a fresh 30 seconds.
   No remaining budget means `not_evaluated`, not a timeout extension. Launch hidden;
   on expiry stop/drain/dispose the child and reject any late response before freeze.

5. **Persist an additive, separate receipt.** Add an optional
   `nonAuthorityDataPathDiagnostics` sibling to the separate companion response
   and explicit final-summary projection; the original primary worker carries
   eligibility only, not computed diagnostics. Arbitrary properties on the primary
   classification are currently dropped by `$newFailureSummary`. The companion
   receipt carries status/reason, elapsed time, input identity, count-domain label,
   diagnostic counts, session/transfer identity and original candidate
   path/line/writer locations/results. Project those identities from the already
   parsed receive record; do not rediscover a candidate by timestamp or adjacency.
   Unavailable counts remain unavailable, not zero. Never overwrite
   `Phase`, `Reason`, `FailureClass`, `FailureVariant`, `ClassificationComplete`,
   `ClassificationSource`, `Coordinator*`, or existing enrichment status/counts.
   Preserve the already-written primary artifact if the optional atomic update
   fails. Finish all writes before hashing/freezing final artifacts.

6. **Gate only this companion path.** Keep the production GUI classifier's existing
   returns and legacy reducer unchanged. Ordinary transfer/runtime behavior and
   successful-GUI processing remain unchanged. Do not add an automatic report run,
   a fallback to LegacyV0 authority, or a second-cycle recovery-selection cutover.

The optional companion fields are an additive private diagnostics contract, not a
change to Snapshot V1, its catalog/profile hashes, normalized boundary schema, wire
DTOs or public runtime APIs. Verify old response consumers tolerate the optional
sibling. If strict finalization validators require a broader schema/receipt redesign,
stop and report it instead of bypassing validation.

## Focused validation and exit

- Parse affected scripts on PS5.1/PS7 and Release-build the smoke project.
- Run new adoption/eligibility/serialization tests and the existing child-process
  failure-enrichment/strict-evidence tests directly affected by the plumbing.
- Cover Snapshot invalid, authority failure, authoritative pass plus unresolved
  data-path evidence, absent eligibility, exhausted budget, child failure, malformed
  output, late output, input mutation and failed optional writes. Primary fields
  and receipt status must remain unchanged in every case.
- Preserve 20 characterized shared/GUI policy cases, the 46-case GUI safety matrix,
  missing-Tuna-receive negatives and generated transport/redaction evidence.
- Compare independently parsed full, regenerated compact and companion diagnostic
  JSON/hash and original candidate locations. Compare primary classifications and
  count domains separately; do not claim global quartet/scalar parity.
- Run one final focused joined gate, including a real isolated child-process
  companion fixture. No whole-report or production-width profiling project.

Exit: an optional companion survives the response/final-summary projection without
altering primary classification, deadline or finalization outcomes; exact shared
diagnostics retain their limited meaning; no late background writes remain.

## Historical finding and next decision

Immutable `20260910T060311Z` remains failed at helper 9564. Its saved
`25/25/18/7`, scalar `14`, and missing exact Tuna receive are not repaired or
reclassified by this plan. Use an equivalent generated fixture; do not rerun the
historical report or reconstruct evidence.

The report allowance is still three consumed and the live campaign 6/6 exhausted.
After this bounded integration passes, review whether a further policy cutover or
GUI-success-path diagnostics is actually necessary. Any new package/preflight/live
allowance, runtime fix, or acceptance-path change requires a separate decision.

Prepared by source review only after commit `9bde1bae`; no implementation, build,
test, evaluator/report or live execution was performed for this plan.
