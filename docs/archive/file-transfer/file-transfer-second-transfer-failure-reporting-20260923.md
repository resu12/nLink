# Second-transfer failure-reporting RCA and bounded fix

## Evidence and root cause

Baseline: `40d5a82b29a4050659bb97a0c92a880fa79f762b` on `v0.7.0`.
Latest live campaign: `artifacts/initial-fallback-owner-live-20260923-40d5a82b/live/20260923T181337Z`.

The first 128 MiB transfer passed integrity. The GUI then failed with `The property 'Phase' cannot be found on this object`, and the persisted summary omitted `secondTransfer`. The parent-owned retained analysis completed with passing snapshot route consistency, but overall acceptance remained `FAIL/correctness/incomplete_proof`.

`Get-TunaGuiFileTransferSetupFailureClassification` intentionally returns null when authoritative snapshots pass and no exact harness classification applies, unless its caller requests a completed classification. The second-transfer catch did not request completion or check the returned value. Reading `.Phase` could replace the original exception before saving the failed phase. The accept-button wait immediately upstream had the same unchecked nullable contract, so it could mask the original timeout before the outer catch received it.

A fresh five-line fixture from real Core decisions and the production snapshot emitter reproduced the nullable result. Executing the actual second-transfer catch under strict mode reproduced the exact missing-`Phase` error on Windows PowerShell and PowerShell 7 before the fix. This establishes the harness defect, not the original live transfer's underlying runtime cause. The live artifact did not preserve that original exception; no historical authority evaluation was repeated to infer it.

## Fix

Only the two error-handling sites in `tools/GuiSmoke-Windows.ps1` change:

1. Save the original `ErrorRecord` before classification and request a completed classification.
2. Validate the returned object using the existing completed-classification contract before reading its properties.
3. In the second-transfer catch, retain an explicitly incomplete pending classification when classification is null, malformed, incomplete, or throws. Record classifier exceptions separately in `classificationError`.
4. Save the second-transfer record with the original error and rethrow that original error. If a measured phase already exists, preserve its observed route, completion, and integrity data; error reporting does not grant successful scenario proof.
5. In the upstream accept wait, preserve the existing contextual error for valid classifications. Otherwise rethrow the original timeout without attempting to format an invalid classification.

The classifier's nullable API, snapshot authority logic, route-publication requirements, route-cycle gate, terminal observations, and first-transfer summary are unchanged. No wallet, transport, performance, retry, or application runtime code changes are included. Classification I/O or deadline hangs are not addressed by this exception/result-shape fix.

## Verification

Evidence directory: `artifacts/second-transfer-failure-reporting-20260923`.

- `red.trx`: two expected failures, reproducing the missing-`Phase` masking error on both hosts before production edits.
- `green.trx`: the outer catch passes on both hosts after its fix.
- `accept-red.trx`: both tests fail the added upstream accept-classification contract before that caller is fixed.
- `final-focused.trx`: 2/2 tests pass on both hosts. Each executes 14 actual second-transfer catch/finally cases and 7 actual accept-catch cases: 42 case executions total.
- Cases cover null, complete fail-closed snapshot classification, complete explicit classification, missing fields, incomplete classification, empty phase, and thrown classifier exceptions. The second-transfer cases cover both absent and already measured phase records, original error identity, persisted JSON, first-transfer preservation, stopped timers, cleanup, and unchanged failed proof state.
- `affected-regressions.trx`: 8/8 selected tests pass: four generic classification response cases, enrichment adoption, authoritative passing-snapshot fail-closed behavior, preactivation accept classification, and separate second-transfer proof contract.
- The focused fixture parses the complete GUI script on both hosts. `git diff --check` passes.
- `Verify-Implementation.ps1` checks the latest live result's 139 preserved pins and records source/test artifact hashes. It verifies evidence integrity, not live acceptance.

No live test, package rebuild, full matrix, commit, or push is performed for this bounded implementation. The previous live failure remains unchanged. Next: review/checkpoint these four source files, rebuild the portable package, then rerun only `second-transfer-after-reactivation` with strict preflights and no retries.
