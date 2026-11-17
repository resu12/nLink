# Security-context lifecycle diagnostic retention

2026-09-25. Branch `v0.7.0`, base HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Finding and bounded fix

The next missing input for reviewing security-context history was retention.
The previous accepted two-writer capture
`artifacts/publication-dispatch-window-20260925/96add5b297e84b8ab268925523b75b79/`
contains installation and retirement diagnostics in both original writer files,
but none of those four records occurs in its retained primary input. The sender
records are at writer sequences 11 and 151; the receiver records are at 13 and
228. The primary retained 339 full records and 162 compact authority records.
These saved artifacts were inspected, not reevaluated or repaired.

The producer already emits both events in
`NknSignalingTransport.SecurityEvidence.cs`, with transport/installation identity,
local installation generation, establishment fingerprint, projected session,
context status and `authority_effect=none`. No additional runtime event is needed
to close this retention gap. The semantic diagnostic allowlist omitted them.

The only production change adds these two names to
`GuiSmokeLiveDiagnosticEvidenceEventNames`:

- `filetransfer_security_context_installed`
- `filetransfer_security_context_retired`

The legacy and Coordinator authority catalogs, parser, streaming copier, source
ownership, original evaluations, runtime and classifier are unchanged. Retention
is unconditional diagnostic capture; it is not a security-lifetime validator.
Incomplete diagnostic records are preserved rather than silently filtered into
an apparently complete history.

## Focused verification

The new `SecurityLifecycleRetention_PreservesOriginalDiagnosticsWithoutAuthority`
test failed on PowerShell 5.1 and 7 before the allowlist change, at the missing
diagnostic-catalog assertion. After the fix, both cases pass. They check exact
original record contents, semantic origins and order, rejection of suffixed
lookalike event names, retained raw-string identity, and no authority or live
publication capability from this synthetic capture. Captured, unbound and
incomplete diagnostics are exercised; these are retention controls, not genuine
security-lifetime proofs. The two existing yield-retention cases also pass.

Fresh original two-writer integration:
`artifacts/publication-dispatch-window-20260925/f4e6c7d8de3f4b82a2a9f8c1f68e8e57/`.

Both PowerShell hosts pass the original dispatch integration and its new
post-run lifecycle-retention assertions. The assertions compare the original
writer/merged records with the retained output. They do not run in the private
consumer, reopen its reader, grant custody or perform another evaluation.
The unchanged original consumer still checks complete source coverage and
custody before and after querying its original full input.

- All four lifecycle records are retained unchanged, at physical source origins
  15, 17, 340 and 343. The original primary has 343 full records and still 162
  compact authority records; its input preparation succeeds.
- Both authority views have 71 passing boundaries and full/compact parity passes.
  There remain exactly three original evaluations with the original callers.
- Forward yield, reciprocal yield and rejection-chain matches remain intact.
  All 10 positive/exclusion and 50 negative dispatch controls pass, together with
  the original custody control, unchanged-input checks and 21 shared witnesses.
- The dispatch interval remains receiver writer sequences 188 through 230, with
  endpoint kind `verified_writer_capture_eof_metadata`. Its last retained row is
  now sequence 228 at source line 343, and it contains 34 retained interval rows.
  This newly retained retirement diagnostic does not redefine that interval as
  a runtime-retirement interval. `RuntimeRetirementProven` remains false.
- `no_matching_bound_dispatch_observed_in_capture` is unchanged. The original
  `missing_exact_tuna_receive` failure remains unrecovered; classifier and
  acceptance remain unchanged.

The focused default-off small-input observer regression also passes on both
hosts, preserving its original report parity and evaluation calls. Six test
cases pass in total: four retention cases and two integrations, each integration
exercising both hosts. No aggregate lane or unrelated runtime test was rerun.

Receipts are under
`artifacts/publication-security-lifecycle-retention-20260925/results/`:
`security-lifecycle-red.trx` is the intentional two-case reproduction;
`security-lifecycle-retention-green.trx`,
`security-lifecycle-original-capture.trx` and
`security-lifecycle-default-off.trx` are the passing verification receipts.
PowerShell parsing and whitespace/diff checks pass.

## Remaining boundary and next step

This closes input availability, not the security-lifetime or broader stale-safety
contract. No new lifetime consumer or classifier was implemented. Existing
diagnostic matches must not be relabeled as lifetime or safety certificates.

Next, review a bounded writer-local lifecycle consistency query over these
original retained records. Bind each selected use to its exact local transport,
installation, generation, establishment and session; do not equate peer-local
IDs or generations. Explicitly handle missing, duplicate, conflicting, retired
and unbound evidence. Preserve original custody, full/compact agreement and the
existing shared 64-witness and cumulative work limits.

Define that query's claim before implementation: diagnostic formatting/log
submission may interleave with context retirement, so writer log order is not
automatically the instant of key use, callback execution or runtime authority
retirement. Security-context retirement is also not Coordinator route-publication
retirement. Neither these records nor a captured dispatch no-match proves absence
of all effects, exact Tuna delivery, durability or acceptance.

The final seal is
`artifacts/publication-security-lifecycle-retention-20260925/FINAL-VERIFICATION.json`.
All 59 other pre-existing dirty files are byte-for-byte unchanged; only the
diagnostic allowlist and targeted integration assertion script changed among
the original 61. All 35 pins in the previous seal remain unchanged. No portable
rebuild, live GUI/NKN run, commit or push occurred. This step neither requires a
live-256 test nor establishes readiness for one.
