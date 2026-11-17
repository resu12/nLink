# Test-only attempt-bound app launch roles

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-attempt-dispatch-binding-20260926.md`.

## Scope

This step associates the original helpee/helper launch requests with the
consumed parent-attempt scope. It does **not** establish two Core writer
closures, actual transfer-file custody, a live transfer, or acceptance.
The native children remain headless PowerShell fixtures on the selected host.
The 256 MiB value is a bound setting, not transferred test data.

Only the explicit `NLINK_TEST_ATTEMPT_DISPATCH` compilation profile exposes
the new APIs. Test copies of the original named request-creation statements
and `Start-AppInstance` call use them. The production scenario dispatch remains
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`.

## Binding

The current original GUI owner can create each named role request once, only
after its original attempt ticket was consumed. The roles are associated
with those exact request objects, not inferred from writer order, output text,
environment labels or report paths. An invalid role, copied owner, or an
unconsumed attempt cannot allocate a role request.

The test-only launch entry selects the existing original in-process owner.
Wrong-role, copied, foreign same-label and repeated requests are refused
before process start. A valid call delegates to the original native
`StartAppWithReports` implementation, preserving its readiness handshake,
process binding, original stdout/stderr readers and ordinary cleanup.

Matching requires the exact retained request, report and process objects;
the report's original native launch; an open native app attachment; matching
actual PID and creation time; and the still-current consumed attempt.
Swapping complete roles or mixing requests, reports or processes fails.
Copied reports and tickets cannot substitute for the originals. Refused
copies cannot dispose or consume the valid binding.

Parent cancellation, changed scope bytes, invalid native app identity,
abandoned child exit and GUI-owner disposal invalidate the corresponding
checks. This is local trusted-workflow launch evidence, not an executable
attestation, same-account security boundary or route-authority token.

## Verification

The initial positive run failed because a test-source replacement matched
both the scenario parameter and its cleanup callback. The replacement was
narrowed to the complete scenario parameter statement. Both host positives
then passed. No production change was needed for that fixture failure.

Final focused and compatibility results are recorded in
`artifacts/frontier-attempt-roles-20260926/FINAL-VERIFICATION.json`.

- `focused-final.trx`: 14 passing tests across Windows PowerShell and
  PowerShell 7, covering the original binding, parent cancellation, changed
  attempt bytes, wrong app PID/creation, abandoned child exit and default-loader
  isolation. Each native case also checks the copied, swapped, foreign,
  duplicate and unconsumed-request refusals described above.
- `compatibility.trx`: 12 passing existing tests: eight selected
  attempt-dispatch/default-loader cases and four scenario valid/default cases.

These final runs contain 26 distinct passes, zero failures and zero skips.
The earlier two positive passes are repeats, not additional final cases.
PowerShell syntax and whitespace checks pass. All six implementation/test
files remained unchanged throughout the final runs.

All 544 previous verification pins matched before editing. The only changed
pre-existing files are the attempt-dispatch partial-type declaration, its
explicit loader and its test GUI dispatch script. The other 223 pre-existing
dirty files remain byte-for-byte unchanged. Three new code/support files and
this report bring the dirty-entry count from 226 to 230. Production source,
the existing headless app/scenario fixtures and the live guard are unchanged.

No aggregate suite, portable build, live GUI/NKN test, full matrix, commit or
push is part of this step. The coordinator, acceptance rules and selected
failure pre-verdict reservation remain unchanged.

## Remaining boundary

Next, join these named original app requests to their independently owned
Core writer identities and completed native manifests. Reuse the existing
app lifetime, file adoption and writer-match path, without treating launch
leases or writer-array order as writer custody. That writer-role join remains
unimplemented here. Actual source/finalized-destination custody and original
full-native analyzer consumption follow separately; the current work does
not enable the live scenario owner or relax the second-Tuna-v4 requirement.
