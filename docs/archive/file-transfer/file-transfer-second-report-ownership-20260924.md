# Second-transfer report ownership: RCA and bounded fix contract

## Status

Bounded reporting implementation is in the working tree. Baseline remains
`309a6abeacd7aa653c6efa95e12fd8a4409c4357` on `v0.7.0`. Runtime code is unchanged;
the changes are confined to primary report publication, its parent/receipt/consumer
handoff, and focused tests. No new live run, portable build, historical authority
reevaluation, commit, or push was performed during implementation.

Latest campaign:
`artifacts/fallback-projection-epoch-live-20260924-309a6abe/live/20260924T083830Z`.
All 136 sealed artifact pins were verified unchanged before investigation.

## What passed and what failed

Both the first 128 MiB transfer and second 16 MiB transfer completed on both sides
with matching saved-file SHA-256 hashes. The parent analysis proves fresh
second-transfer `file_tuna_v4` / protocol 4 publication generation 1 in both
directions. Primary snapshot evidence, stream completeness, full/compact parity,
route consistency, writer integrity, and receipt validation pass. No first
proof-chain failure or unresolved route boundary is reported.

The campaign still fails. Its two failure messages describe one absent artifact:
`second-transfer-analysis/filetransfer-route-consistency-summary.txt`. The dedicated
analysis directory is absent, while the second-transfer retained slice exists and
is 10,919,244 bytes. The current-runtime proof is true and one finalized attempt
is selected because the delegated manifest currently checks only the primary
analysis. This does not override the campaign's failed acceptance result.

The primary operator result is `WARN_EXTERNAL_TRANSPORT`, with zero hard failures,
for fallback receiver-state churn. Performance PASS and efficiency warning are
separate from the missing-report failure. No performance tuning is indicated.

## Root cause

The ownership migration moved the primary retained analysis to `RouteAcceptance`
without transferring responsibility for the second-transfer report.

1. `Invoke-TunaRouteAcceptanceRun` passes `RetainedAnalysisOwner=RouteAcceptance`
   to the GUI wrapper (`tools/Run-FileTransferRouteAcceptance.ps1:9207`, 9319).
2. The wrapper's successful second-transfer report path is guarded by
   `RetainedAnalysisOwner=Wrapper`
   (`tools/Run-FileTransferTunaGuiSmoke.ps1:2739`, 2744, 2750). Delegation correctly
   prevents duplicate analysis there, but also omits its secondary report.
3. The live parent invokes the primary analysis once and reconciles its receipt
   (`tools/Run-FileTransferRouteAcceptance.ps1:9378`). It does not request or publish
   a second-transfer-scoped result from that analysis.
4. Acceptance still requires the secondary route and operator reports (4212,
   4213, 4881). The wrapper's delegated receipt list deliberately contains only the
   primary directory (`tools/Run-FileTransferTunaGuiSmoke.ps1:2356`). Parent receipt
   reconciliation therefore cannot detect the missing scoped report by itself.

The first missing edge is **primary analysis -> second-transfer-scoped publication
and validation**. It is not coordinator route admission, file delivery, or fallback
retirement. Do not change runtime code to address it.

The existing `FileTransferPhase5SecondTransferTests` use the fake acceptance path.
That path explicitly creates and analyzes `second-transfer-analysis`
(`tools/Run-FileTransferRouteAcceptance.ps1:5622`, 5649), so a passing fake matrix
test does not exercise the real delegated ownership path.

## Deterministic reproduction

`artifacts/second-transfer-report-ownership-rca-20260924/Probe-Ownership.ps1`
extracts and executes the actual parent orchestration function, actual wrapper
ownership branch, and actual split-proof acceptance guard. Child launch, analyzer
calls, and receipt reconciliation are recorded stubs. No GUI, network, wallet,
coordinator authority evaluation, or historical analysis is invoked.

Three characterization cases pass on each of Windows PowerShell 5.1 and PowerShell 7:

| Case | Observed current behavior |
| --- | --- |
| Wrapper-owned control | Primary and second-transfer analysis calls are scheduled |
| Delegated live orchestration | Wrapper schedules neither; parent schedules only the primary and reconciles its receipt |
| Missing secondary report | Acceptance returns false despite the expected first-transfer route cycle |

These six characterization passes reproduce the gap. They are not green fix
regressions or live acceptance. The stubs do not validate analyzer semantics;
the sealed current-HEAD live campaign supplies that evidence.

## Bounded implementation contract

1. Keep one primary analysis owner and the existing full/compact authority
   evaluations. Add a second-transfer report request to that same invocation.
   Publish the scoped result from its existing in-memory evaluated proof data;
   do not start a second authority evaluator or reload serialized summaries to
   manufacture a new authority decision. Keep legacy standalone/fake ownership
   behavior outside this delegated path unchanged.
2. Bind the requested scope to the same run, scenario attempt, immutable source,
   and second-transfer capture window. Derive the exact coordinator session and
   transfer identity from source-linked primary evidence. The GUI's redacted
   `[redacted]` transfer ID is not a usable identity. Reject absent, ambiguous,
   foreign, overlapping-first-transfer, or incomplete scope; do not choose the
   last transfer, largest transfer, latest route, or first matching timestamp.
3. Require matching full/compact parent evidence and complete paired second-transfer
   lifecycle coverage. Publish the exact scoped route selections, protocol and
   publication identities, terminal/data-path proof, and applicable warning/failure
   outcomes. Preserve shared session facts. Never copy a whole-run PASS as a scoped
   verdict. A primary failure, unresolved shared failure, missing proof, or scope
   ambiguity remains fail-closed; the scoped report must not erase it through the
   existing split-proof override.
4. Produce both currently required route and operator views from that one scoped
   result. Include the measured second-transfer repair/recovery context needed by
   contamination checks. Do not satisfy only file existence while silently making
   missing operator or measurement data appear clean. Do not copy first-transfer
   warnings or counters as if measured on the second transfer.
5. Bind the scoped output hashes and scope/source identity into the primary-owned
   receipt/manifest validation. It is a derived result, not an independent analyzer
   attempt. Publish completion only after all required output is complete; partial,
   missing, changed, wrong-attempt, or stale output cannot be accepted. Preserve the
   original primary failure and the existing route/protocol acceptance requirements.
6. Do not backfill or reclassify the sealed live campaign. Its result remains FAIL.
   Validate publication on fresh deterministic inputs, then a new targeted live run
   after review/checkpoint and a fresh portable build. No full matrix before that gate.

Expected production scope: parent report request/handoff in
`Run-FileTransferRouteAcceptance.ps1`, scoped emission alongside the existing primary
analysis in `FileTransferOps/AnalyzerOrchestration.ps1` (a small focused helper if
needed), and receipt/consumer validation in `FileTransferSoak/RetainedAnalysisEvidence.ps1`
and the relevant acceptance consumer. No `src/` runtime changes are indicated.

## Focused verification plan

1. Turn the delegated omission reproduction into a red/green regression. Assert one
   parent invocation, unchanged full/compact evaluator call counts, zero secondary
   evaluator launches, and both required scoped outputs from the same primary.
2. Use fresh two-transfer authoritative fixtures and both PowerShell hosts. Verify
   paired route 4/Tuna publication, exact terminal/data-path scope, and no mutation
   of parent evidence, first-transfer results, or raw source lines.
3. Negative controls: missing/ambiguous/redacted-only identity, wrong session or
   attempt, reordered/changed/foreign source membership, first-transfer overlap,
   missing direction/terminal, fallback-v6 or invalid Tuna-v6, incomplete stream,
   parity mismatch, parent failure, shared failure, and stale/partial/changed reports.
4. Exercise actual live-owner orchestration with recorded child/evaluator calls,
   plus receipt reconciliation and split-proof/contamination consumers. Keep the
   fake-path case as a control, not a substitute for the delegated-path regression.
5. Run only these affected lanes once after stabilization. Then review/checkpoint,
   rebuild, and run `second-transfer-after-reactivation` outside the sandbox with
   strict preflights and zero implicit retries. Preserve the full-matrix gate.

No runtime defect beyond the already fixed epoch binding is asserted here.

## Implementation

- The delegated second-transfer scenario requests its derived report through the
  existing primary worker's environment. Other scenarios clear that request; the
  legacy wrapper/fake analysis paths remain unchanged.
- `SecondTransferReport.ps1` receives the primary's actual full/compact evaluated
  objects, parity, route projection, GUI completion, and failure gates. It never
  invokes an authority evaluator. Parent/shared failures block publication.
- The semantic event catalog was extracted without changing its contents so the
  GUI capture and derived view use the same event membership. Exact ordered source
  membership, a gap-free window, paired coordinator starts/completions, complete
  boundaries, and the preceding paired transfer bind the second transfer identity.
- Retained session-terminal/efficiency records and publication-linked matching
  send/receive payload fingerprints supply terminal/data-path evidence. Raw
  `transfer_terminal` is not in the primary semantic capture and is not used to
  manufacture evidence. The second GUI payload size and matching SHA are bound too.
- The completion descriptor contains exact publications, terminals, input/capture/
  GUI hashes, attempt identity, and hashes of route, operator, repair, measurement,
  and scoped-log outputs. The primary receipt validates this descriptor; delegated
  manifest reconciliation and split-proof acceptance require it.
- Recovery contamination uses the bound source window through the last paired
  terminal, not a legacy scan that expects excluded raw terminal markers. Missing
  or altered measurement output fails closed. First-transfer counters are not
  copied to the second report.

Validation artifacts are under
`artifacts/second-transfer-report-implementation-20260924`. Tests use modeled
evaluated views for rejection controls and fresh production Coordinator/emitter
snapshots with explicitly modeled physical delivery for the positive integration
control. These are offline tests, not claims of live transfer success.

Final focused lane: **16 passed, 0 failed**, recorded in
`results/focused-final.trx`. This includes the new publisher/owner/fresh-snapshot
tests on Windows PowerShell 5.1 and PowerShell 7 (41 rejection controls per host,
receipt/output tampering, measured repair/recovery context, and zero evaluator
calls by the publisher), existing receipt and route-consistency tests, catalog
checkout/loading, and both legacy fake second-transfer acceptance controls.
The test build used an isolated artifacts path; the portable package was not rebuilt.

All 136 pins in the preceding live seal remain unchanged. Its verdict remains
FAIL; no scoped report was backfilled. The RCA seal's three implementation-source
pins are intentionally superseded (analyzer orchestration, receipt validation,
parent acceptance), along with this updated handoff document. Its remaining seven
artifact pins are unchanged.

Next gate: review/checkpoint this reporting change, rebuild the portable package,
then run only `second-transfer-after-reactivation` with strict elevated preflights
and no implicit retry. Do not run the full GUI matrix before that targeted gate.
