# Regular-NKN compact reporting contract

2026-09-22. Base: `144e89ab7ff32c482f6d86b2308b82daa5142776`.

## Evidence and scope

The latest single `regular-nkn-v4-64mb` run is
`artifacts/regular-terminal-live-20260922-144e89ab/live/20260922T042925Z/`.
Its GUI cycle completed with matching payload hashes and both coordinator
stream closures. The original retained operator result and local snapshot
parity passed. The parent acceptance did **not** pass: the NKN summary writer
threw and the capture was not finalized. The original run remains unchanged.
See that campaign's `RESULTS.md` for hashes, identities and exact witnesses.

This implementation addresses only the first reporting contract mismatch.
It does not claim to fix the separate capture-finalization gap or validate
the accumulated changes with another live run.

## Cause

`Invoke-RouteAcceptanceChildScript` requests compact analysis for live runs.
`Invoke-FileTransferRetainedAnalysis` deliberately returns `Summary = $null`
in that mode; the full summary is not part of its compact result contract.
`Write-FileTransferLiveNknSummary` nevertheless dereferenced that full summary,
first failing at `BatchSentAsBatchCount`, then depending on many additional
fields and event collections. Supplying a default zero for that counter would
neither repair the contract nor preserve the evidence.

## Implemented boundary

- The NKN caller explicitly requests a reporting projection through the
  optional `NknReportingSummary` reference on the original analysis call.
- Full mode reuses its already-computed summary. Compact mode projects only
  the observations needed by the NKN writer from the same parsed input.
- `New-FileTransferRetainedSummary -ReportingOnly` shares the original event
  selection, terminal handling, time window, frame counts and batch-counter
  fallback, then returns before route consistency or gate evaluation.
- The compact result still has `Summary = $null`; callers that do not request
  the projection do not build it. Its work is included in total stage timing.
- The writer requires this reporting input and continues using the original
  `Analysis.GateResult`. Timeout, integrity, topology and baseline handling
  remain unchanged. No missing-summary zero-fill or acceptance relaxation.

No runtime, coordinator, transport, queue, performance policy, wallet or
installer code changed. No live artifact was reanalyzed or rewritten.

## Verification

Focused receipts: `artifacts/regular-harness-contract-20260922/`.

- The initial compact/full fake-run regressions reproduced the exact live
  exception for both completion and progress-timeout cases before the fix.
- The fixed reports agree field-for-field between full and compact analysis
  for regular success, regular timeout and mixed-mode success, excluding
  per-run executable path and timestamps. Timeout stays a failing result.
- Projection controls cover protocol 4/6 evidence, terminal present/absent,
  nonzero failure/retry counts, global-event time-window exclusion, batch-count
  fallback and empty evidence. A throwing route evaluator proves that the
  reporting-only branch cannot enter route evaluation; its fields also agree
  with a full-summary control.
- Baseline regression remains failing in both modes. Independent snapshot
  evaluation and stage-publication controls are checked separately.

The first build attempt was blocked by Avalonia build-telemetry logging outside
the sandbox before tests ran. Subsequent focused builds suppress that build
task with `-p:UsedAvaloniaProducts=`; no project configuration was changed.
An intermediate new compact baseline case incorrectly required full-only
throughput artifacts. Its assertion was narrowed to preserve the existing
full-mode check and verify the compact baseline/operator results directly.
The snapshot-ownership control also had a pre-existing fixture gap: it executed
the compact writer's evaluation statements without initializing their primary
input variables. The production selectors were already present at base HEAD.
The fixture now executes those actual selectors and checks their exact output
before retaining the same independent-evaluation assertions. No production
snapshot selection or evaluation changed.

Latest receipts cover **10 unique passing tests**, with no remaining failed,
skipped or aborted case. This is not an aggregate-suite result. All three
modified PowerShell files parse, and `git diff --check` passes. Receipt history,
including intentional and intermediate failures, is in the artifact
`RESULTS.md`. Changes are uncommitted; no portable build, live rerun or push
was performed in this step.

## Next bounded contract: producer finalization

The regular GUI scenario currently ends with `Copy-FileTransferLiveLogSlice`,
unlike the existing producer-owned `Write-FileTransferGuiLogLedgerEvidence`
path. The NKN child wrapper also lacks the finalization-state/deadline lifecycle
owned by the Tuna wrapper. Adding a ledger writer call alone is insufficient.

Before a live rerun:

1. Establish the regular child's attempt-bound state and initial ledger/writer
   bookmark, preserving environment restoration and failure cleanup.
2. Reuse the existing producer finalizer after scenario processes stop and
   redirected output drains. Preserve success and failure evidence; require
   capture hash, writer/cursor validation and finalization state to agree.
3. Add focused actual-finalizer controls for success, scenario failure,
   output-drain/finalization failure, and wrong-attempt identity. Ensure a
   missing or invalid finalization cannot become a selected current attempt.
4. Review the parent handoff for the original retained-analysis receipt and
   snapshot parity. Do not fabricate evidence, rerun authority evaluation or
   retroactively finalize the old capture.
5. After these controls pass, review/checkpoint, rebuild the portable, then
   repeat only regular-NKN 64 MiB with strict preflights. No full matrix or
   live-256 is indicated yet.

The reporting correction is not a substitute for those finalization proofs.

Follow-up: the producer/wrapper implementation and offline verification are
recorded in `file-transfer-regular-gui-finalization-20260922.md`. The separate
parent generic-parity projection remains the next bounded reporting step.
