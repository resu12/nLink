# Caught-up fallback frontier to Tuna handoff — 2026-09-22

## Finding and evidence boundary

The shared stall-grace handoff analyzer rejected an otherwise exact
current-publication handoff when the receiver was caught up: highest received
was `frontier - 1`, rather than at or beyond the next missing chunk.

The latest live capture belongs to checkpoint
`42151c2eb37d98e46a2da79de7705f8e67a0014f`, campaign
`artifacts/phase5-live256-20260922-42151c2e/live/20260922T194011Z`, scenario
`regular-v4-live-activation-off-on-off-256mb`. Its original primary attempt
`2de464834d394dfea988bc9ad4fa6e01` completed normally and reported 30 distinct
stall-grace obligations: 29 recovered, one unresolved. Overall/correctness
remains FAIL/incomplete_proof; configured performance acceptance was PASS.

The primary-selected source witness is retained-log line 21657, writer sequence
11364, wake 410, frontier 4264, highest 4263. Previously inspected lines 21710,
21726, 21949 and 21961 show the inbound repair grant, exact dispatched wake,
repair retirement for Tuna reactivation, and second coordinator-published
Tuna-v4 respectively. This is not evidence that the timer never ran.

No historical capture was replayed or given another authority evaluation. The
synthetic regression proves this exclusion is a contract defect; it does not
prove that removing it is sufficient to accept the historical run. The original
receipt and verdict remain unchanged.

## Contract review and bounded fix

`Get-FileTransferStallGracePublishedSourceAdmission` already admits
`highest >= frontier - 1`. Runtime `IsInboundV6FrontierStalledLocked` explicitly
supports a caught-up receiver after the normal resend gate; frontier repair
range construction also handles that state. The downstream Tuna handoff helper
instead required `highest >= frontier`, excluding this valid shape.

For an exact `CurrentPublishedSource` admission only, the shared handoff helper
now uses `frontier - 1` as its lower bound. Highest must parse as an integer;
missing/malformed values cannot act as a numeric sentinel. Historical
repair-progress admission retains its existing `highest >= frontier` bound.

All existing checks remain required: canonical publication admission, exact
owner/scope, post-deadline grant, primary delivery/dispatch/send, matching wake,
dispatched repair retirement for Tuna reactivation, coordinator transition
commit, applied disposition, and exact Tuna-v4 publication. Conflicting evidence,
wrong identities, missing edges, stale diagnostics alone, and unrelated progress
cannot close the obligation. No coordinator/runtime authority, retry policy,
transport, throughput threshold, or UI behavior was changed.

## Verification and next gate

The synthetic fixture uses frontier 41/highest 40 with no historical repair
progress reaching that frontier. It requires the exact handoff publication as
the recovery event, exercises existing negative handoff mutations, and checks
legacy/indexed, serialized full/compact, and GUI/retained consumer parity on
Windows PowerShell and PowerShell 7. Invalid highest values and the unchanged
historical-admission bound receive explicit negative checks.

Test receipts are under
`artifacts/phase5-live256-20260922-42151c2e/closure-review/tests`:

- `caught-up-red.trx`: both hosts rejected the complete exact chain before the fix.
- `caught-up-green.trx`: both hosts passed after the bounded fix (2/2).
- `handoff-contract-regression.trx`: directly affected contract regression lane
  passed (10/10), including the unchanged historical-admission bound.

`git diff --check` passed. All 75 previous final-verification pins were checked:
73 remained unchanged; the only changed pinned files are the handoff helper and
its regression fixture. No new live run, full matrix, commit or push was made.

The next runtime gate is a newly built portable package and one fresh targeted
live-256 attempt with strict preflights. Do not reinterpret the previous capture
as passing or start the full GUI matrix on the strength of synthetic tests.
