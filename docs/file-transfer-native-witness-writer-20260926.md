# Native writer acquisition for the selected-witness adapter

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-selected-terminal-integration-review-20260926.md`.

## Implemented boundary

A default-off test compilation profile now provides
`Worker.TryOpenNativeWitnessWriterForTest`. It duplicates the already-adopted
collector writer handle into the original worker's one-shot `WriterReader`.
It neither reopens a path nor hashes/reads the candidate before returning it.
The unchanged membership verifier performs the one EOF/hash pass afterward.

The adapter checks the original worker, collector owner, completed source,
primary custody and exact context/arrays/views/native inputs. It requires the
original running cumulative clock, remaining prototype record budget and a
current collector owner, and rejects full-Frontier requests. The primary's
private custody stays inside the worker; it is not passed to the adapter by an
evaluator or resolver. Failed or repeated acquisition never clears the slot.

Only `NLINK_TEST_NATIVE_WITNESS_ADAPTER=1` includes the new guarded helper in
the isolated terminal test loader. The observation hook rejects combinations
with collector membership, terminal payload consumption or pre-verdict
integrity testing. The existing borrower/native-profile refusal remains in
place. Production loaders, production source, witness/verdict ordering,
acceptance rules and the pre-verdict reservation guard are unchanged.

## Scope adjustment discovered during implementation

The review proposed validating the adapter with the small seeded-failure
fixture. Its existing native collector handoff is not available independently:
`TerminalCollectorPrimary.cs` requires the original `HeldFileNativeParent`,
and that owner requires real log/source/destination files and a transfer
binding. The seeded publication fixture has no such payload owner.

This implementation therefore proves only the acquisition primitive using the
existing real 256 KiB Regular-v4 fixture and its original native collector
handoff. It does not fabricate a terminal owner, merge fixtures, enable a
combined borrower profile, or claim the reviewed integration positive.
Connecting this primitive at the original selected-failure witness seam still
requires a suitable same-owner producer/handoff fixture. Both constructor
retention paths and the post-witness terminal join remain unchanged.

## Focused verification

The two new acquisition tests pass on Windows PowerShell 5.1 and PowerShell 7.
They use source index one deliberately, rather than the old index-zero lookup,
and explicitly report `SelectedFailureProven=false`. There is no synthetic
failure object and no original selected witness or publication completion.

Each test rejects 13 substitutions/predicate controls before opening: copied
worker, copied collector, copied source, copied context, foreign clock, stopped
clock, expired clock, full-Frontier flag, changed full/compact arrays, changed
view, changed native owner and absent private custody. Reflection mutations are
restored and are labeled predicate controls, not genuine alternative histories.

Both successful runs observe 93 source rows and one 135-row writer EOF read.
Membership work is 363 visits; cumulative acquisition/membership work is 1078
visits and 677239 code units, with 180-189 ms cumulative work. No limits were
raised. The returned reader begins unverified with zero reads; reopening through
either the new adapter or legacy API is rejected. Disposing the reader retires
it without disposing the original primary or collector, and cannot reopen it.
The probe never installs its reader in `Context.WriterReader`.

Both runs preserve the original three authority evaluations and failed
`FAIL_PROTOCOL_OR_INTEGRITY` verdict. The fixture's real payload/terminal
observations are not consumed by this adapter test as a joined capability.

The first run retained two failures: PowerShell 7 rejected a duplicate helper
inclusion in the loader, and Windows PowerShell reached a missing observation
because the new probe was not invoked. These were test wiring defects, fixed
without changing runtime code. Both affected cases passed on rerun.

The test project builds with 257 warnings and zero errors. Guarded native code
also compiles inside both isolated hosts. Modified PowerShell scripts parse.

## Regression and preservation

All **15 distinct targeted tests pass** in their latest relevant runs: two
native acquisition positives, four default-loader absence cases and nine
existing-path compatibility cases. The compatibility lane ran once and passed
all 13 cases. The first two acquisition failures remain recorded; across all
three TRX files there are 17 executions, 15 passes and two failures.
The focused compatibility lane covers default-loader API absence, the original
borrower and mixed-profile refusal, collector membership and payload joins.
No original witness/completion aggregate is rerun: those implementations and
their native compilation branch are unchanged.

All 14032 effective inherited pins matched before work. Five existing
test/support files change, four test/support files and this report are added.
The other 175 baseline dirty files remain unchanged. The six changed prior
pins are those five test/support files and the rebuilt test DLL; the other
14026 match. HEAD/branch remain unchanged, with 185 dirty entries.
PowerShell parsing, touched-file whitespace and `git diff --check` pass.
The final seal is `artifacts/native-witness-writer-20260926/FINAL-VERIFICATION.json`.
No portable build, live test, full GUI matrix, commit or push is performed.

## Next step

Provide the selected-failure fixture with its own continuously adopted native
collector output owner, then replace only the original witness acquisition
call under an explicit supported combined profile. Validate its unchanged
selected witness/completion and retained membership before adding post-witness
terminal/payload consumption. Do not loosen the terminal file-owner contract to
make the seeded fixture fit, and do not remove the mixed-profile or pre-verdict
guards before their corresponding positive exists. This milestone is not
live-256 readiness.
