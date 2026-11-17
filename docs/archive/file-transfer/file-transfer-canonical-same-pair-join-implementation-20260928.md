# Canonical same-pair join: test-only ownership milestone

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: ownership/scope helper implemented and focused checks passed.
**Combined primary and actual-pair handoff wiring remain pending.** No native
same-pair success or readiness for a full-size run is claimed.

## Bounded implementation

`Support/FrontierCanonicalSamePairJoin.cs` adds a test-only assertion to the
existing partial evaluation custody. The attempt loader includes it; ordinary
loaders do not. The only other existing-source edit makes the test-only held
pair class partial. Production source and dispatch behavior are unchanged.

`TryCheckCanonicalSamePairWitnessForTest` requires the exact issued transfer
evaluation and evaluation pair, exact original witness batch, original payload
set and payload object, and an original resolved diagnostic state. Copies,
foreign owners, retired custody, unresolved batches and repeated joins are
rejected. It uses the original locks, cumulative work clock and visit budget;
checks native input/held-file currentness before and after scope comparisons;
and closes the original inputs on an admitted failed check. It retains no new
object references and issues only a test boolean, not a transferable capability.

Scope comparison requires the canonical SHA, distinct held file identities,
the existing exact UTF-8 `fte1-` session/transfer evidence domain, both retained
writer lineages, and specifically the helper receiver's process/creation and
manifest. The payload must be the same original manifest entry, with singleton
completion/marker sequence ranges ordered before that receiver's drain. Either
peer having a matching receipt is insufficient. Exact native handle identities
and 268,435,456-byte lengths remain checked by the original held-pair currentness
path. No whole-file rehash, receipt-file reopen, new analyzer evaluation, payload
capture or resolver invocation is added.

The method's scalar receipt arguments must come from the trusted combined
primary's already captured and decoded original payload. Scalars alone cannot
create the required native objects. The probe that supplies them is not yet
wired; its original receipt-object provenance and unchanged selected failure,
witness lines, views, output commit and reports remain explicit integration gates.

## Verification and limitations

Artifacts: `artifacts/canonical-same-pair-join-implementation-20260928/`.

- New focused tests: 7 passed. In both `pwsh` and Windows PowerShell, the actual
  test loader compiles the helper, 29 synthetic scope controls pass, and 16
  synthetic ownership/budget rejection controls pass. Ordinary-loader exclusion
  passes in both hosts; a source guard checks original budget/currentness order
  and the absence of recapture, re-resolution or file rehashing.
- Directly affected existing gates: 5 passed once, covering ordinary-loader
  transfer-evaluation exclusion and the unchanged pre-launch composite block.
- Total: **12 tests passed, zero failed**. The 45 internal controls per host are
  not counted as additional xUnit tests. Script parsing passed. The new test file
  emitted no compiler warnings; unrelated existing warnings were left unchanged.

The private synthetic controls deliberately contain no file handles, receipt
text or native authority. Scope positives prove comparison behavior only;
ownership controls prove rejection, original-budget refusal and no clock reset.
They do not establish a positive native join, complete transfer, receiver timing,
native cleanup or full-input budget compliance. Those require the original
combined primary and later separately gated offline run.

All 3,682 pins and 541 dirty entries matched at entry. Before-images preserve
the two edited files and five pinned build binaries. Final seals record the
bounded changes and new evidence. No live/full-size transfer, GUI matrix,
commit or push occurred; acceptance, timing and authority flags are unchanged.

## Next implementation step

Compose the existing actual-pair binding and late-publication probes in one
original primary worker. Bind once at Capture; use only its original conditional
resolver; call this helper immediately after resolution with the original batch
and decoded payload; retain selected-failure/witness/report/output/cleanup checks.
Then extend only the exact named profile's GUI source/destination, writer
finalization, wrapper/parent and primary-selection paths. Reuse the actual-pair
finalizer once, never run the small diagnostic finalizer afterward. Preserve all
original limits and give the new composite its own stop-before-acceptance sentinel.
Keep the existing pre-launch dispatch block until focused composition/handoff
tests and review pass. Do not run a full-size transfer merely to exercise this
currently unwired helper.
