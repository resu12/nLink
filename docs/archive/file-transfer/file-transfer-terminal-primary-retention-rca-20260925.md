# Terminal evidence: primary retention gap and bounded fix plan

2026-09-25, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-terminal-source-tokens-20260925.md`.

## Result

The next integration attempt found an earlier missing edge than the proposed
file-owner handoff: the genuine `transfer_terminal` records are removed by
semantic retention before original primary analysis. This milestone adds a
default-off test observer and two real-transfer characterization regressions;
it does not fix production retention or establish a primary integrity positive.

Both tests complete a fresh 256 KiB Regular-v4 transfer, validate the existing
private source/file owner, and hold its original operational log read-only
through the original writer finalizer and retained-analysis CLI. There is no
synthetic terminal record, GUI success summary, copied earlier route attempt,
new authority evaluator or JSON capability input.

| Actual stage | PowerShell 5.1 | PowerShell 7 |
| --- | --- | --- |
| Original writer records | 135 | 135 |
| Genuine terminal source lines | 127, 131 | 126, 131 |
| Terminal records in finalized writer evidence | 2 | 2 |
| Terminal records in semantic/analyzer input | 0 | 0 |
| Original primary full/compact records | 91 / 16 | 91 / 16 |
| Original authority evaluations | 3 | 3 |
| Rejected custody controls | 6 | 6 |
| Cumulative diagnostic work | 86 ms | 83 ms |

Fresh attempts under `artifacts/terminal-source-tokens-20260925/`:

- PowerShell 5.1: `de1eb274-02d1-4b91-b3e6-eb7dee2901e0/`.
- PowerShell 7: `0f4d7fe8-ccd9-46ad-80dd-1be2b90b2d6e/`.

In both, `primary/terminal-source-primary-observation.json` reports
`Status=unproven`, `FirstMissingEdge=terminal_records_not_retained` and
`TerminalIntegrityGate=unproven`. Original primary results remain
`INCONCLUSIVE`, gate `fail`, `guiCompleted=false`, `guiIntegrityOk=false`.
These small offline runs are not the canonical live route-cycle scenario.

## Root cause and acceptance consequence

`Get-GuiSmokeSemanticEvidenceEventNames` in `tools/GuiSmokeEvidenceCatalog.ps1`
unions the pinned LegacyV0 catalog, Coordinator categories and explicit live
diagnostics. None includes the exact event `transfer_terminal`.
`filetransfer_transfer_terminal` is a different catalog entry.

`GuiSmokeLogLedgerStreamingProcessor.cs:945` writes semantic records only when
their event name is in that set. `GuiSmoke-Windows.ps1:4300` copies the semantic
slice into `filetransfer-retained-log-slice.log`. The two exact original terminal
lines survive in writer evidence but not either semantic or analyzer input.
The primary's source hashes, native identity and custody are valid: it correctly
analyzes the incomplete projection it was given. This is not loss in logging,
redaction, file transfer, parsing, or the new terminal tokens.

A catalog addition is not acceptance-neutral. The compact analyzer currently
infers both GUI completion and `guiIntegrityOk=true` from inbound/outbound
completion-looking events (`AnalyzerOrchestration.ps1:3080-3119`), without
measuring the persisted destination. Consequently simply retaining these events
can activate an existing integrity inference. Tokens correlate a transfer;
they do not prove file bytes or transfer file-owner custody.

## Proposed next implementation — approval required

Approve these two bounded, linked changes together, not an alternative
acceptance cutover:

1. Retain the exact `transfer_terminal` event as non-authority diagnostic
   evidence in the explicit GUI retention list. Preserve original line text,
   ordering, writer sequences and source lineage. Do not alter the pinned
   LegacyV0 catalog, append synthetic records, or expand retention indiscriminately.
2. Stop synthesizing `guiIntegrityOk=true` from terminal status pairs. Preserve
   independently supplied GUI integrity evidence and existing completion/status
   reporting; a terminal pair without file-integrity evidence must remain
   non-passing. This is an explicitly reviewed tightening of inference, not a
   claim that the GUI summary itself completes the future owner handoff.
3. Change these characterization tests into exact original-line retention
   positives on both hosts, while the separate terminal-integrity handoff stays
   unproven. Add focused controls for missing/duplicate/foreign terminal pairs,
   canceled or errored transfers, and absent/false GUI integrity. Keep existing
   independently supplied integrity behavior covered. Verify the terminal
   events alone cannot make acceptance pass.
4. Preserve original evaluator counts, route/protocol checks, independent
   failure factors and all existing work/witness limits. Run affected retention
   and terminal-inference tests, then the directly affected original-primary
   regressions. No full matrix or live run follows from this change.

Afterward, the same-attempt file-owner handoff, complete scenario scope,
five-route cycle and closed independent-failure inventory remain separate
obligations. Do not combine this transfer with the earlier Tuna-pair fixture
or reinterpret existing publication/analysis-completion receipts.

## Verification and limits

Accepted focused receipts in `artifacts/terminal-primary-source-20260925/results/`:

- `terminal-primary-final.trx`: both new host regressions passed.
- `terminal-primary-regressions.trx`: eight source-binding/redaction cases and
  the existing default-off original-primary observer test passed (9 tests).

Total: **11 distinct tests passed**. The default-off test executes both hosts;
neither emitted the new probe output. The six controls per new host reject a
copied context, absent completed-evaluation pair, copied full/compact arrays,
changed physical origins and absent context. Original view contents remain
unchanged, and the real file/source binding is rechecked after analysis.

The source request binds the analyzer copy's native identity through its held
read lease. The writer completion intentionally has no native identity for that
later copy; comparing against its null descriptor would be incorrect.
The primary does not independently verify writer membership in these runs
(`WriterMembershipVerified=false`), and the observer does not reopen the log to
manufacture it. Exported observations are post-run test assertions, not a new
cross-process capability or whole-scenario receipt.

Development failures are retained separately: `terminal-primary-initial.trx`
selected full instead of compact analysis; `terminal-primary-compact.trx`
exposed the genuine missing terminal pair; `terminal-primary-gap.trx` used the
wrong identity descriptor in the new harness. Only the two accepted receipts
above support the final pass count. No acceptance rule was weakened to obtain it;
the new tests characterize the still-unfixed retention defect.

Only test code/support and this document changed. Production, classifier,
acceptance, prior artifacts and unrelated work are preserved. No portable build,
live test, commit or push ran. Verification pins and preservation counts are in
`artifacts/terminal-primary-source-20260925/FINAL-VERIFICATION.json`.
