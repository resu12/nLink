# Regular-NKN GUI producer finalization

## Evidence and scope

This follows the compact-reporting correction documented in
`file-transfer-regular-harness-reporting-20260922.md`. Source HEAD remains
`144e89ab7ff32c482f6d86b2308b82daa5142776`; the changes are uncommitted.

The latest relevant live attempt is
`artifacts/regular-terminal-live-20260922-144e89ab/live/20260922T042925Z/regular-nkn-v4-64mb`.
Its transfer completed, but parent acceptance failed with no finalized current
attempt. The regular producer only copied a log slice, and its wrapper did not
own an attempt-bound finalization journal. A successful transfer and SHA match
do not replace those missing evidence edges.

The old retained input remains unchanged, SHA-256
`9e8e0b4b68f45475beb58e6e2cbafcb3ac4dc0a43a478f6085527474bc2a5a82`.
No old artifact was retroactively finalized or reclassified.

## Implemented contract

1. The regular wrapper creates the attempt-bound journal and passes its path and
   scenario-deadline request path only to the child. It does not inherit another
   caller's private writer/frontier custody request.
2. The GUI producer establishes its real ledger/writer bookmark after helpee
   startup. Both regular modes reuse the existing producer-owned finalizer.
3. The finalizer receives the exact scenario processes, performs operational-log
   cleanup and output drain, then performs its existing final sync, capture,
   immutable publication and writer/cursor validation. No second evaluator or
   new route/runtime authority is introduced.
4. The wrapper waits for child exit and redirected output EOF before marking
   `gui_child_exited`, checks exact journal identity, and preserves the child's
   last stage. The original scenario timeout is unchanged; a bounded 120-second
   cleanup grace permits finalization, not another transfer phase.
5. Hard timeout records permanently non-authoritative evidence and returns 124.
   A child-recorded timeout or timeout artifact cannot be cleared by normal exit.
   Scenario failures survive simultaneous finalizer failures, and scenario
   environment restoration remains in `finally`.
6. A finalization journal blocks later synthetic appends and global-log fallback
   copying, including when finalization fails. Existing journal, timeout,
   deadline request, ledger or raw capture rejects artifact-directory reuse
   before cleanup can delete any original evidence.

This changes harness evidence ownership only. Coordinator/runtime, transport,
performance, wallet and GUI connection behavior are unchanged.

## Verification

`artifacts/regular-finalization-fix-20260922/RESULTS.md` records the commands,
receipts and intermediate failures. Latest receipts contain **23 distinct
passing tests**, with no failed, skipped or aborted test:

- 14 new offline producer/wrapper controls: success, scenario failure, drain
  failure, wrong writer, wrong journal, hard timeout, finalizer failure,
  simultaneous failures, timeout monotonicity and five artifact-reuse markers.
- 9 existing affected checks: journal monotonicity, delayed output drain,
  exact direct-manifest identity (two hosts), Tuna finalizer call ownership,
  NKN public surface and three compact/full NKN report comparisons.

The new controls execute the production regular scenario function, wrapper,
finalizer and direct-manifest writer. They use real child processes and files,
but simulated GUI/app observations: they are not live GUI or NKN evidence.
They verify sealed hashes and parent proof rejection, including exact timeout
child termination. A finalized failed scenario remains a failed scenario.

Four changed/new PowerShell files parse; `git diff --check` passes. No aggregate
suite, portable build, live run, commit or push was performed in this step.

## Remaining parent reporting handoff

Read-only inspection confirms a separate projection mismatch:

- `Assert-Phase4RouteSummary` reads generic
  `coordinator_snapshot_full_compact_parity_verdict` from the route summary text.
- That text emits the distinct route-consistency parity key instead. The
  original `filetransfer-coordinator-snapshot-parity-summary.json` reports
  generic parity PASS with equal 36-boundary hashes/counts.
- Therefore the parent generic parity projection defaults to `missing` despite
  the original parity artifact. Do not alias route-consistency parity into the
  generic result or rerun authority evaluation to fill this reporting gap.

The direct regular-manifest path already consumes the producer's finalization
evidence. The strict retained-analysis receipt branch is specific to Tuna; this
step does not manufacture or impose a Tuna receipt on regular attempts. The
parent's existing completed-proof validation also reads the original full,
compact and parity artifacts. The projection mismatch has not been established
as an independent regular acceptance gate failure.

Next bounded step: correct that parent projection using original same-attempt
primary artifacts, with missing/malformed/conflicting-artifact controls. Then
review/checkpoint the accumulated harness changes, rebuild the portable and
rerun only regular-NKN 64 MiB with strict external preflights. Do not advance to
the full matrix or live-256 on the basis of these offline results.

Follow-up: the parent projection is now corrected and verified offline in
`file-transfer-parent-parity-reporting-20260922.md`. The next boundary is the
combined pre-commit review/checkpoint and fresh-build targeted live validation.
