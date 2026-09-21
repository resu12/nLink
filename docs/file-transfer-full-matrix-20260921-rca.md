# Full Phase 5 matrix: bounded RCA and fix plan

2026-09-21. Source checkpoint: `bdfd45b964956867b02f3f00ae8b12981a8a270a`.
Status: the bounded blank-line diagnostic fix is implemented and verified
offline; paired readiness is now deterministically reproduced with a bounded
follow-up contract plan. Remaining runtime fixes are pending. No new live run or
historical authority reevaluation; original matrix verdicts are unchanged.

## Evidence boundary

The fresh, strict-preflight six-scenario campaign is
`artifacts/phase5-full-gui-matrix-20260921/live/20260921T051528Z`, acceptance ID
`f7198928-af55-4009-8b4c-651a2a6886ea`. All attempts failed acceptance. Source
provenance passes; campaign current-runtime proof and finalized manifest
validation do not. Preserve those verdicts and the earlier targeted passing
run independently. Full results are in
`artifacts/phase5-full-gui-matrix-20260921/RESULTS.md`.

The matrix's five harness/evidence failures are outcome classifications, not
proof that their underlying runtime behavior was correct. In particular, the
helper-payer scenario contains an earlier Coordinator readiness rejection.
Four performance failures have missing measurements represented as zero;
they do not demonstrate a throughput regression.

## A. Live-256: missing exact Tuna delivery, not guard bypass

Original primary attempt `d31e053d81c44e038725bbba17b31405` completed without
timeout, exit 0. Its verdict remains `FAIL_PROTOCOL_OR_INTEGRITY`, variant
`second_tuna_rejected_regular_ingress_exact_tuna_resend_missing`.
Snapshot integrity, full/compact parity and the complete route cycle pass;
transfer completion and SHA also pass. They do not close this data-path edge.

The primary's quartet is 73 raw/distinct, 40 recovered and 33 unresolved
data-path candidates; the separate mismatch scalar is 66. These are diagnostic
record counts, not counts of corrupted files or independently proven defects.
This RCA adjudicates only the first selected candidate.

Retained source: the live-256 directory's `filetransfer-retained-log-slice.log`.

| Retained line | Observation |
| --- | --- |
| 8808 | Sender `p33912r098c96f37d1f4866`, sequence 10881, sends chunks 4936-4938 / 64512 bytes over regular NKN, route epoch 0, security sequence 1696. |
| 19800 | Sender transitions fallback-v6 to second Tuna-v4, epoch 3. |
| 20029 / 20054 | Receiver publication/effect selected by the original primary-owned local-publication diagnostic. |
| 20047 | Receiver's matching fallback-v6 to Tuna-v4 service transition. |
| 20068 / 20069 / 20071 | Receiver sequences 10350/10351/10352: receive, mismatch, ignore; all rejected before dispatch with authority effect none. Another writer's intervening physical row does not break the receiver sequence. |
| 21594 / 21670 | Sender/receiver transition to final fallback-v6, epoch 4. |
| 22370 / 22481 | Fallback-v6 send and receive of chunk 4936. |
| 22668 / 24486 | Fallback-v6 send and receive of chunks 4937-4939, covering the other two rejected chunks. |

The original send and rejected ingress share envelope fingerprint
`ftenv1-2dc4bc83485ffa19faaa76291a24e4363c2051c83c33d2f5b5c64db6d4753d4d`,
payload fingerprint
`ftpay1-4201605b3ef819d6633a8c7f4da18bf05687c5273b190f5279e257ef61bfd83d`,
security sequence, range and byte count. This supports delayed original
regular ingress, about 44 seconds after its send, not a new regular send under
the second Tuna publication. The original diagnostic binds local receiver
publication/effect/use; it explicitly does not evaluate peer lineage.

Read-only range inspection found 38 send/receive records overlapping chunks
4936-4938 and **zero Tuna transport records** among them. Thus this is not an
exact-batch matcher overlooking a differently batched Tuna delivery. Later
fallback repair explains eventual completion, but does not meet the current
exact-Tuna recovery predicate.

`tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1:820` onward requires
an exact Tuna send and receive between the earlier regular send and the rejected
candidate for a fallback-to-Tuna transition. Lines 951-956 select the missing-
receive or missing-resend variant. The current report follows that rule.
The dispatch guard continues to enforce rejection without recovery authority.

The GUI milestone log also records that step 4 skips a progress wait after the
second Tuna publication and switches off about 1.2 seconds after observing it.
That is test sequencing, not proof that it caused the pre-rejection missing
resend: the flagged ingress is already earlier. Simply delaying switch-off
cannot retroactively satisfy this candidate's required ordering.

**Conclusion:** confirmed first missing proof edge, not a demonstrated new
runtime misroute or proven analyzer false positive. No runtime resend, guard,
timeout or acceptance-rule change is justified by this evidence alone. Do not
schedule repair from the rejected callback, substitute fallback/SHA evidence,
or silently widen the recovery window. Any alternative stale-rejection
disposition needs an explicit contract decision and Coordinator-owned
continuity/lineage proof with negative controls.

## B. Initial Tuna readiness is asymmetric in the other Tuna cases

| Scenario | Diagnostic answer line | Actual measured-transfer start |
| --- | ---: | --- |
| active-tuna-v4-64mb | 450, receiver/helper | Regular-v4 on both peers, lines 510/524; both later Completed. |
| live-switch-off-helpee-64mb | 419, receiver/helper | Regular-v4 on both peers, lines 470/488; both later Completed. |
| live-switch-off-helper-64mb | 341, sender/helpee | Outbound Tuna-v4 at 399; inbound Coordinator rejects at 414 for `initial_tuna_readiness_missing`; offer declined. |
| second-transfer-after-reactivation | 437, receiver/helper | First transfer starts regular-v4 at 486/528; canceled after the Tuna-publication wait fails. The requested second-transfer contract was never reached. |

These are each scenario's own retained line numbers. The helper case's
`filetransfer_legacy_negotiation_rejected` at 415 records `offer_route_not_active`;
outbound terminal at 441 is Declined, before the later empty-Lines exception.

`Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer` at
`tools/GuiSmoke-Windows.ps1:33030` accepts one unscoped answer observation from
the combined app log. Its caller at 36517-36521 proceeds to the measured
transfer. It does not establish current usable initial-Tuna evidence at both
local service boundaries. This helper is explicitly diagnostic, not authority.

`SessionFileTransferService.cs:1331` captures initial readiness locally before
route selection; `ResolveInitialOutboundRouteSelection` at 5836 can choose
regular when usable local evidence is absent. The adapter provider at
`NknSignalingTransport.Acceleration.cs:8287` checks local transaction/snapshot,
lease and answer evidence; the Coordinator remains the admission authority.
The matrix directly demonstrates asymmetric readiness, but does not yet prove
which provider/transaction transition should be changed. Longer arbitrary waits,
matching opposite-peer generation numbers, or treating answer ACK/listener
readiness as authority would not be a justified fix.

The helpee switch-off classifier's authoritative-snapshot pass covers the
observed regular route; it does not prove the requested Tuna route existed.
Active Tuna and second-transfer classification enrichment timed out. Do not
replace those incomplete original classifications with a new historical run.

## C. Confirmed blank-line classifier defect

The helper case's four process-output files each contain a blank line.
`Read-AppLogLinesSafe` (`GuiSmoke-Windows.ps1:1957`) splits text at newline
without removing empty records; a trailing newline alone can create one.
`Read-GuiSmokeProcessOutputLinesSnapshot` at 1581 preserves them. The live
classification branch at 6509-6531 combines app/process lines and calls
`Get-FileTransferCoordinatorSnapshotEvaluation -Lines`.

That mandatory `[string[]]` parameter at
`tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1:10326` allows an empty
collection, but not empty string entries. PowerShell rejects the argument
before the evaluator body, masking the original accept-control timeout after
the Coordinator's offer rejection. The shared event-name parser at 718 and
the classifier-local parser also reject an empty `Line`, so adding only the
array attribute would leave another failure immediately downstream.

Isolated binding tests reproduce both failures on Windows PowerShell and pwsh.
The first dependency of the evaluator was deliberately stubbed to throw on
entry: no snapshot authority evaluation was executed. A preliminary hypothesis
that the two simple object-array log helpers rejected blanks was disproved;
those helpers correctly return false for empty/blank/unrelated inputs.

**Smallest proposed fix:** permit blank entries at these input boundaries and
interpret blank event-name input as no event, preserving original indexes and
record order. Do not filter/reindex the captured input or weaken validation of
nonblank snapshot records. Add mixed blank/nonblank, empty, malformed nonblank,
missing snapshot and full/compact source-location regression cases.

## D. Regular-NKN terminal evidence remains unresolved

The regular case has outbound Completed at line 12319 and outbound snapshot
stream completion at 12324. The inbound stream starts at 304, but the retained
and raw operational logs contain neither its terminal event nor stream closure.
The original evaluator reports
`snapshot_stream_incomplete:fte1-f748cb7b8fb52c45c0e72827`. The receiver file
reaching full size is insufficient to infer terminal publication or SHA proof.

No exception explaining that missing edge was found in the checked operational
log. Do not call it only a slicing failure or modify runtime finalization yet.
The next deterministic investigation should trace inbound finalization through
Coordinator terminal transition, snapshot publication, app terminal projection
and writer drain. Preserve failure when any edge is absent.

## Fix sequence and stop conditions

1. **Blank-line diagnostic fix (completed offline):** implement only the three confirmed input
   boundaries and focused regression cases. Verify blank records remain
   non-events, source indexes stay intact, and malformed nonblank evidence
   still fails. No live rerun is needed for this slice.
2. **Paired initial-readiness reproduction (completed):** use the existing fake-network,
   real-service/Coordinator fixtures for both payer roles. Capture each peer's
   provider result and Coordinator decision at offer/admission; hold delivery
   deterministically. Include missing, expired, consumed and wrong-direction
   evidence. Fix only a reproduced handoff edge; if the harness lacks a
   trustworthy readiness observation, design that observation without granting
   authority or changing a preactivated scenario into an activation cycle.
3. **Live-256 contract boundary:** preserve the exact-resend negative case.
   Reproduce the delayed original-envelope/second-Tuna/final-fallback sequence
   with real Coordinator publications before proposing a runtime correction.
   If safe rejection plus Coordinator-proven later-route delivery needs a new
   disposition, review that contract explicitly; do not implement an exemption
   under the current plan. The other 32 unresolved candidates remain unadjudicated.
4. **Regular terminal chain:** reproduce the first missing inbound terminal
   edge before editing the service or evidence capture. Investigate incomplete
   finalized-attempt selection separately; never mark a failed/uncompleted
   attempt as a completed primary just to satisfy the manifest.
5. After a bounded implementation stabilizes, run only affected lanes, then
   rebuild the portable before the targeted affected GUI scenario. No full
   matrix repeat until targeted gates pass or advance to a documented new edge.

No throughput, queue, retry-cadence, wallet, installer, screen-sharing or generic
GUI invite changes are included. No new live run, commit or push in this step.

## Verification and custody

Artifacts: `artifacts/phase5-full-matrix-rca-20260921/`.

- `Characterize-HarnessEdges.ps1`: 15 synthetic characterization cases verified
  on each PowerShell host. Final logs are
  `harness-characterization-complete-powershell.log` and
  `harness-characterization-complete-pwsh.log`. The earlier disproved-assumption
  run is retained as `harness-characterization.log`, not counted as a pass.
- One directly relevant existing test passed (26 seconds):
  `AnalyzeRetained_SecondTunaRejectedIngressDiagnosticPreservesExactRecoveryContract`.
  It exercises the exact-recovery diagnostic matrix on both hosts. Used the
  existing Release harness with `--no-build --no-restore`; no new C# build or
  aggregate test claim. TRX: `exact-resend-contract.trx`.
- Read-only rehash matched all 11 original live-256 receipt-bound outputs and
  input SHA256 `d73c3199ae548b8322be3aae0b30d6d5977616b9ccf8000387ce376909045798`.
  Receipt SHA256 remains
  `469db472d0c3f7124f0e9be4ea8a5b98f07916f3b75978a26391720932408368`.
- No historical analyzer, recovery predicate or authority consumer was rerun
  over the live artifact. Line/range inspection and original-report reading
  are diagnostic only; original verdicts remain unchanged.

## Blank-line implementation checkpoint

Only three production parameter declarations changed: `AllowEmptyString` on
the shared snapshot event-name parser's `Line`, the snapshot evaluator's
`Lines`, and the classifier-local event-name parser's `Line`. Existing parsing,
record ordering, index assignment and authority checks are unchanged. No
blank-row filtering, route-policy changes or recovery exemptions were added.

The existing dual-host diagnostic regression now covers empty/blank-only and
missing-snapshot inputs, malformed nonblank evidence among blank rows, explicit
compact-record indexes, full/compact boundary parity, and both immutable and
app-log/process-output classification paths. A malformed record remains rejected
as `snapshot_instance_missing` at its original zero-based index 3; each GUI
classification still performs exactly its original two evaluations. Inputs
without snapshots retain `LegacyV0` with incomplete stream and no boundaries,
not Snapshot V1 authority.

Verification: `artifacts/snapshot-blank-lines-20260921/RESULTS.md`.

- Before the fix: both host cases reproduced the empty-string binding failure.
- After the fix: both host cases pass. An intermediate run caught an incorrect
  new test expectation (`SnapshotV1Authoritative` instead of the existing
  `SnapshotV1Shadow` for malformed evidence); only that test assertion changed.
- Six directly affected snapshot/classifier regression cases pass, including
  strict route-cycle publications, malformed/invalid V1 rejection, retained
  handoff rejection, diagnostic isolation, and indexed log ingestion.
- Tests used the existing Release C# harness with `--no-build --no-restore`;
  it loads the edited PowerShell sources from the repository at execution time.
  No C# changes, aggregate suite, portable rebuild, GUI/live run, commit or push.

The paired initial-readiness reproduction in step 2 is now complete; see
`docs/file-transfer-paired-initial-readiness-20260921-rca.md`. It reproduces both
payer-role outcomes and identifies that pre-transfer runtime-answer acceptance
requires an absent Coordinator source publication. The separate design is now
recorded in `docs/file-transfer-session-preparation-contract.md`; transfer-scoped
authority guards remain strict. Its Core model and service lifecycle/admission
bridge are implemented and tested, as is the distinct authenticated wire
boundary, exact-generation physical lane startup/ownership/cleanup and Core-bound
physical execution lifetime. Isolated preparation handshake execution and
event-purpose isolation now pass 194 distinct focused cases; see
`artifacts/session-preparation-handshake-20260921/RESULTS.md`. Exact lease
observation and admission-time physical handoff now pass 175 distinct cases;
see `artifacts/session-preparation-admission-20260921/RESULTS.md`. Public purpose
selection now passes 202 current cases; see
`artifacts/session-preparation-request-20260921/RESULTS.md`. Non-consuming paired
diagnostics now pass 171 cases (35 new, 136 existing); see
`artifacts/session-preparation-diagnostics-20260921/RESULTS.md`. Fresh portable,
strict external-sandbox preflights and targeted initial-Tuna GUI validation are
next. The public unlock now selects Core-owned pre-transfer preparation and the
harness requires both peers' scoped observations; this is not a passing live
acceptance result. No full matrix rerun occurred at this checkpoint.
The subsequent single initial-Tuna live checkpoint passed fresh-package and
strict network/GUI preflights plus paired readiness, but failed snapshot route
consistency and made no payload progress. See
`docs/file-transfer-initial-tuna-live-20260921-rca.md` and
`artifacts/session-preparation-live-20260921/RESULTS.md`. The original matrix
verdicts are preserved; the dirty-source targeted run is diagnostic only.
The diagnostic fix prevents an empty log row from masking the earlier failure;
it does not resolve readiness or the other matrix proof gaps.
