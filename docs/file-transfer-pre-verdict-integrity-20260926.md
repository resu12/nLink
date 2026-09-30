# Test-only pre-verdict integrity integration

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the bounded shadow milestone in
`file-transfer-pre-verdict-integrity-review-20260926.md`.

## Result

The owned terminal/payload join can now be consumed before the original
compact verdict, under an explicit default-off test flag. Twenty distinct
tests pass: twelve new tests and eight existing-path compatibility tests.

This is still a single-writer, 256 KiB, offline Regular-v4 fixture. Production
runtime/coordinator code, original analyzer predicates and reports, GUI
acceptance, parent scenario acceptance, and alternative acceptance are unchanged.
All transfer-backed shadow cases retain `FAIL_PROTOCOL_OR_INTEGRITY` and
the independent failed live-route proof. Neither an owned payload nor GUI
integrity true promotes the incomplete route cycle.

## Implemented path

`NLINK_TEST_PRE_VERDICT_INTEGRITY=1` enables the new test observer only.
The original GUI summary reader is instrumented in memory at exact checked
branches to distinguish observed absence, successful parsing and read/parse
failure, while preserving its return behavior and single read. Shadow
classification also checks the raw property's Boolean type and presence;
it does not trust the original compatibility Boolean conversion.

The observer validates the unique pre-verdict seam, the unchanged original
verdict block and the fixed 42 named count predicates. Immediately before
`$verdict = 'PASS'`, it performs the existing owned membership/origin/payload
work once and invokes a small separately one-shot consumer alongside the
guarded `TerminalPayloadJoin.cs` implementation. Cleanup emits detached
observations after the original reports rather than repeating that work.
The consumer rechecks exact original worker/files/reader, custody/pair,
context, arrays/views and running cumulative clock. Copied diagnostic
results, a replacement clock and historical issuance cannot supply authority.

The independent inventory contains 58 named scalar factors: the five
incomplete-proof and 37 route/runtime count inputs, plus 16 completion,
protocol, route, liveness, warning, candidate-count, snapshot/parity and
family-selection inputs. Warning kinds and exceeded kinds are separate
bounded arrays. Original unfiltered candidates, the selected-first reference,
and raw/effective mismatch candidates are kept distinct while projecting
bounded diagnostics, including recovered states. No proof matcher, recovery
or coordinator authority evaluation is repeated. There is no overall
`would_pass` or acceptance token.

GUI shadow behavior is explicit:

- Observed absence or a parsed Boolean true plus current owned evidence:
  `owned_integrity_proven`, for the bounded fixture only.
- Boolean false plus current owned evidence: `conflict`, never an override.
- Boolean false without owned evidence: `explicit_failure`.
- Missing/null/wrong-type property, unreadable/malformed input or unknown
  read status: `invalid_input`.
- Otherwise, absent/true input without owned evidence:
  `owned_integrity_unproven`.

`Consumed` records the owned consumer operation; it does not mean GUI
conflicts or any other gate passed. The original
`TerminalIntegrityGate=unproven`, acceptance flags and missing-production-
integration label remain separate from this shadow.

## Additional writer-slot safeguard

Source review found an important later consumer:
`LocalPrimaryPublicationWitness.ps1:68` can call the original one-shot
`TryOpenWriter` for a selected proof failure. Early membership verification
must not consume that slot and thereby change the original witness outcome.

Accordingly, the new shadow declines **before any source/membership work**
when `firstCompactProofChainFailure` is non-null, recording
`integrity_shadow_selected_failure_writer_reserved` through the existing
optional-probe error diagnostic. Original analysis continues; no shadow
success is issued. Both hosts verify that this branch does not invoke the
source probe and preserves the original continuation. An exception before
the optional seam is likewise not masked by deferred cleanup.

This is a deliberate fail-closed scope boundary, not a fix to the production
witness workflow. Candidate-history controls use explicitly synthetic
objects; they do not claim a genuine selected-failure transfer has been
integrated with both consumers. A future same-attempt integration must
resolve that shared writer ownership without reopening, replaying analysis,
or fabricating a failure witness.

## Work and cleanup

No limits were increased: payload 262,144 bytes, packet 16 KiB, log 4 MiB,
4,096 cumulative record visits, 20-second cumulative optional work, and the
existing code-unit and boundary/witness bounds.

The inventory checks remaining record budget before candidate traversal and
remaining code-unit budget before charging string inspection. The owned
consumer independently rechecks both remaining budgets. The genuine
no-candidate fixtures add 58 visits and 13,540-13,544 code units. Successful
transfer-backed observations, including rejected-consumption controls, use
1,308-1,324 cumulative visits and 263-367 ms of cumulative work.

Every successful transfer-backed test reports three original authority
evaluations, no additional writer reads and no primary payload hash pass.
Cleanup retires current access. Historical consumption is output-only and
cannot restore a result after ownership ends.

## Verification

Results: `artifacts/pre-verdict-integrity-20260926/results/`.

| TRX | Passed | Failed | Purpose |
| --- | ---: | ---: | --- |
| `pre-verdict-initial.trx` | 0 | 3 | Exposed empty-array handling error |
| `pre-verdict-controls.trx` | 2 | 0 | Corrected reader/inventory/seam controls, both hosts |
| `pre-verdict-native.trx` | 0 | 10 | Exposed evaluation-counter script-scope error |
| `pre-verdict-corrected.trx` | 3 | 0 | Corrected native positive and both-host controls, including remaining-budget preflight |
| `pre-verdict-final.trx` | 9 | 0 | Remaining transfer-backed positives and rejection cases |
| `pre-verdict-regression.trx` | 10 | 0 | Final both-host controls, including selected-failure writer reservation, plus eight existing-path cases |
| `pre-verdict-final-guard.trx` | 2 | 0 | Both genuine positive hosts after the final writer-slot guard |

There are **20 distinct passing tests**. Across all 39 attempts, 26 passed
and 13 failed; development failures are retained, not reclassified.
The two observer defects were an empty conditional array becoming null under
strict mode, and an external script trying to read its caller's script-scoped
evaluation counter. The fixes preserve an explicit empty array and pass the
diagnostic counter from the original invocation. Neither was an app/runtime
transfer failure.

The twelve new tests cover both genuine positive hosts, explicit GUI conflict,
agreeing GUI integrity with an independent route failure, added record/code-
unit limits, post-join expiry, disposed membership, a stopped clock and a
failed payload join. Each genuine path also rejects twelve ownership/reference
substitutions and repeated consumption.

Both-host standalone controls exercise ten actual reader inputs, unknown read
status, all 42 count factors individually, simultaneous independent failures,
multiple/recovered mismatch candidates, missing factors, changed candidate
identity, insufficient budgets, changed/duplicated seams, early exceptions
and selected-failure writer reservation. These are diagnostic predicate and
in-memory controls, not live hostile histories or new route evidence.

The compatibility lane runs both hosts for original primary-source,
collector-primary, collector-membership and post-report payload-join paths.
It ran once after implementation stabilized. Only the directly affected
positive hosts were repeated after the final guard; no aggregate suite ran.

The build succeeded with `--no-restore -p:UsedAvaloniaProducts=`:
257 warnings, zero errors. Guarded native helper sources compile in each
isolated PowerShell host. Changed PowerShell files parse successfully and
`git diff --check` passes.

## Preservation and next boundary

All 11,010 preceding pins matched at entry. Nine previously dirty test/support
files changed; the other 155 baseline dirty files are preserved. Five new
test/support files and this report were added. The rebuilt smoke-test DLL is
the only other changed prior pin. Shared production source and previous
evidence files are unchanged.

The final seal is
`artifacts/pre-verdict-integrity-20260926/FINAL-VERIFICATION.json`.
It carries forward prior pins with the explicit changed-source/DLL inventory
and records the new test artifacts, including failed attempts.

Next is the same-attempt production handoff contract: equivalent real GUI
producer custody, actual parent scenario/size scope, non-conflicting writer
ownership for selected failures, and all independent route-cycle evidence.
Acceptance adoption remains a separately approved change. Do not combine the
Regular-only fixture with a different Tuna fixture to claim a canonical run.

No portable rebuild, network/GUI run, live-256 test, full matrix, commit or
push was performed.

