# Production parent payload handoff

The production parent now receives the original payload pair before native
writer forwarding, validates it after wrapper exit and output drain, and keeps
a valid pair alive with the original exported native custody. Optional payload
rejection preserves the ordinary completion and native custody without taking
either a second time. This is an offline integration checkpoint, not live
route-cycle acceptance.

No application routing, acceptance rule, goodput policy, timeout, portable
package, live preflight, GUI/NKN run, commit, or push changed in this phase.

## Starting state and implementation

The predecessor is
`artifacts/production-wrapper-payload-handoff-20260930/FINAL-VERIFICATION.json`,
SHA256 `6625b631358cdd1c6d89eea75cd54835a379f2c7310300e77c7f290a8f8ad9af`.
All 690 dirty entries matched before editing. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with no staged changes.
Before-images are retained for all seven existing files touched. Six match
predecessor pins; the previously clean scalar-relay fixture matches its HEAD
blob after the repository's normal text filtering.

The parent starts its private payload owner from the original relay reports
and polls it before the existing native-writer pull. The wrapper offers its
original bound pair before `TryOfferAndWait`. Optional startup, pull, or offer
failure cannot replace the ordinary launch or forwarding outcome.

Inspection found that the strict parent take can consume the ordinary native
completion before rejecting payload writer association. It also disposes native
custody on rejection. Calling that strict API directly from production would
therefore lose an otherwise valid ordinary result.

`TakeRelayAfterExitWithPayloads` shares the strict checks while retaining the
original result and whether its take was attempted. In this production
composition, a failed optional association closes the pair but leaves native
custody alone. A valid association leaves the original one-use custody export
to the existing caller. The strict `TryTakeAfterExit` API still detaches custody
itself on success and disposes it on rejection. Guard order, identities, file
checks, and cumulative work budgets remain unchanged.

After a successful original export, the native custody's existing cancellation
lifetime owns the payload pair. Without that export, local cleanup disposes the
pair. A non-null completion is not evidence of an accepted payload pair, and
neither object gains routing or publication authority.

## Offline verification and limitations

Fifteen distinct latest test outcomes passed: two parent caller contracts, five
real-handle parent cases, the guard-order regression, three strict parent-pair
cases, two wrapper caller contracts, and two ordinary relay tests. Each ordinary
relay test covers 25 scenarios on its PowerShell host. No aggregate suite or
full GUI matrix was run.

Normal-loader checks execute the actual production start, pull, terminal,
cleanup, and wrapper-offer blocks with controlled failures on both PowerShell
hosts. Five real-handle cases execute the production calls with the preceding
GUI and wrapper integrations: valid and foreign-writer pairs on both hosts,
plus a disposed pair on PowerShell 7. They verify original completion identity,
one-use export, rejection isolation, continued file protection at the analysis
boundary, and final release of both 256 MiB files.

The integration fixture still uses test-augmented native loading and headless
role processes. It stops at the analysis boundary. It does not prove a complete
normal-loader GUI transfer, primary transfer evaluation, or a live Tuna route.
Older component fixtures retain their explicit payload calls; the new caller
tests do not substitute production ownership calls.

Initial integration attempts exposed two new fixture errors: a mixed-separator
artifact path failed the exact native root check, and a probe success marker
entered the wrapper's result pipeline. Both were corrected in the fixtures.
The final five integration cases passed without a production logic repair.
Failed-run evidence is retained rather than overwritten.

The focused test build completed with 339 warnings and zero errors. Build
escalation was limited to the existing Avalonia build-service requirement;
tests stayed offline in the normal sandbox. The phase verifier checks latest
test outcomes, predecessor pins and dirty work, before-images, recorded child
termination, released payload files, PowerShell syntax, and whitespace before
writing an immutable seal.

The first audit stopped on a reused Windows process ID. Its current process
started after the saved test-worker record, proving it was not that child.
The cleanup audit now distinguishes this case by process start time and retains
the reused IDs in its result. No unrelated process was stopped.

## Next primary verification

The native primary transfer-pair code and analyzer binding already exist.
Review and exercise those production calls with this original exported parent
custody, including rejection and cleanup, before treating that integration as
verified. Do not replace it with another handoff mechanism.

A reviewed checkpoint, fresh portable build, strict escalated preflights, and
targeted live validation still follow. Acceptance continues to require the
second coordinator-published `file_tuna_v4` route; payload integrity alone is
not sufficient.

Evidence and the final verification seal are under
`artifacts/production-parent-payload-handoff-20260930`.
