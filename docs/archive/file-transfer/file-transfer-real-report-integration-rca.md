# C1q: real-report integration prerequisite fixes

2026-09-14, branch `v0.7.0`, base HEAD `ef78e9d7`, accumulated work uncommitted.

## Scope and observed failures

The C1p relay and C1o adoption matrices exercised actual ownership code, but
their positive workflow/report fixtures did not execute the entire retained
report writer. The next offline integration probe executed the actual
`FileTransfer-Ops.ps1 -Mode AnalyzeRetained` entry point on a current-source,
offline generated log. It found two report-generation blockers before witness
selection could be verified:

1. PowerShell 7 failed in `Write-FileTransferRetainedAnalysisStageJson` because
   `$isWindows` is case-insensitively the same name as its read-only automatic
   `$IsWindows` variable. The only change is renaming the local platform flag;
   atomic replacement, retries, error propagation and cleanup are unchanged.
2. Windows PowerShell reached the fallback-tail reducer, then failed while
   accessing `BindingKind`. An empty array emitted from an `if` assignment
   becomes `$null`; piping that variable into `Where-Object` supplies a null item
   under strict mode. The fix materializes the entire conditional output with
   `@(...)`, preserving a real empty array. Nonempty binding predicates and
   authority/proof rules are unchanged. Both hosts reproduced this error.

Focused tests reproduced three failures out of four before the fixes and pass
all four afterward. This is an analyzer RCA, not an RCA for runtime/NKN behavior.

## Combined offline check now verified

A newly generated 128 KiB fake-network transfer, using the actual operational
logger through drain, is passed unchanged through:

- the actual RouteAcceptance relay launcher;
- a controlled offline Wrapper substitute, actual hard-deadline GUI parent and
  actual GUI ledger finalizer;
- the accepted one-hop typed GUI completion;
- the actual RouteAcceptance retained-analysis parent and original
  `FileTransfer-Ops.ps1` entry point, producing real full/compact/parity reports;
- the original terminal receipt write and actual optional adoption attempt.

The retained parent and real analyzer are exercised on Windows PowerShell 5.1
and PowerShell 7. The relay launcher retains its original Windows PowerShell
child selection. Only test loader paths, retained analyzer host selection,
hidden-window configuration and a boolean adoption observation are instrumented.
The 1800-second production watchdog is not changed. No report, Snapshot view,
failure selection or completion is substituted in this combined check.

The normal fixture has no primary-selected proof-chain failure. Both invocations
therefore omit the optional publication. They produce `completed` analysis
receipts with unchanged inputs and authoritative Snapshot parity `pass`, while
the operator verdict remains `INCONCLUSIVE` and `gate_status=fail`. Report
completion is not route-cycle acceptance. Each parent makes its original two
source hashes, two source line counts and one terminal receipt write; zero
synthetic events are added. The test prohibits parent authority evaluation.
It does not add a separate worker evaluation counter or claim new live proof.

Evidence: `artifacts/publication-offline-integration-20260914/RESULTS.md`.

## Remaining integration boundary and next bounded step

C1r has now completed the positive full-Wrapper/real-reports gate on both
PowerShell hosts. See [the positive integration result](file-transfer-positive-publication-integration.md).
The text below records C1q's historical stopping point; top-level opt-in
persistence remains the next missing edge, and no live caller is enabled.

This is a verified omission path, **not completion of C1p's full combined gate**.
The controlled Wrapper substitute does not execute the complete Wrapper main
script, and the normal generated transfer cannot prove positive adoption of a
failed-proof witness because the primary selects no failure.

Next, prepare a deterministic offline failing-source fixture whose actual first
reported failure is an eligible publication-use event with exact A/B1 local
evidence. Exercise the full Wrapper main workflow with explicitly controlled
offline GUI/setup dependencies, then the real retained report writer and
post-receipt adoption. Preserve original failure ordering and the private two
evaluation custody. Do not inject a synthetic selected-failure object, choose a
later eligible event, fabricate reports, enlarge the input bounds or weaken
authority checks to obtain a positive result. If a fault is seeded in a test
source, label it explicitly as an offline fixture, never live runtime evidence.

Only after that positive gate should top-level opt-in consumption/persistence
be considered. No top-level live caller is enabled. No portable rebuild, live
GUI/NKN/256 MiB test, commit or push was performed or is required at this point.
