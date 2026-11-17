# Terminal retention and integrity-inference fix

2026-09-25, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the two bounded changes approved after
`file-transfer-terminal-primary-retention-rca-20260925.md`.

## Changes and result

`tools/GuiSmokeEvidenceCatalog.ps1` now retains the exact `transfer_terminal`
event as a diagnostic. Original text, ordering, writer identity/sequences and
physical origins are preserved. It is not added to an authority category.
The pinned LegacyV0 catalog is unchanged.

`tools/FileTransferOps/AnalyzerOrchestration.ps1` no longer sets
`guiIntegrityOk=true` when it recognizes a terminal status pair. Compact
analysis preserves separately supplied GUI-summary integrity evidence and
existing completion reporting. Status alone cannot supply that integrity bit.
This is the approved tightening of inference, not adoption of the alternative
acceptance contract. No Service, Coordinator, transport or route behavior changed.

Both fresh genuine 256 KiB Regular-v4 transfers now deliver their exact two
terminal records through the original writer finalizer into original primary
custody. Each source contains 93 records instead of the previous 91. The
primary recognizes completion but correctly reports integrity false without
separate evidence, with verdict `FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`.

| Original-primary observation | PowerShell 5.1 | PowerShell 7 |
| --- | --- | --- |
| Operational records | 135 | 135 |
| Terminal lines in original writer | 129, 131 | 129, 131 |
| Terminal lines in retained primary input | 88, 90 | 88, 90 |
| Full / compact records | 93 / 16 | 93 / 16 |
| Original authority evaluations | 3 | 3 |
| Rejected custody controls | 6 | 6 |
| Cumulative diagnostic work | 73 ms | 76 ms |

Fresh attempts under `artifacts/terminal-source-tokens-20260925/`:

- PowerShell 5.1: `3700ac74-f574-4140-bb5f-2457aa2743ca/`.
- PowerShell 7: `e01f6c67-e10b-44ef-8486-7599647ab45c/`.

The observer emits `nlink.test-primary-terminal-source/v2`, status
`verified_primary_terminal_source_only`, and first missing edge
`file_owner_handoff_unavailable`. Its separate `TerminalIntegrityGate` remains
`unproven`. `ProbeChangesAcceptance=false` means the observer does not mutate
the primary result; it does not deny the approved production inference change.

## Verification

All **13 distinct focused and directly affected tests passed**. Receipts are
under `artifacts/terminal-retention-fix-20260925/results/`:

- `terminal-retention-focused.trx`: four tests, covering retention and the
  actual compact inference block on both PowerShell hosts.
- `terminal-retention-primary.trx`: two genuine original-primary integrations.
- `terminal-retention-contract.trx`: seven regressions: explicit GUI integrity
  with post-terminal liveness; existing full-analyzer generic completion;
  both fresh-checkout catalog tests; default-off observer parity; and both
  strict Tuna-pair cases (each exercising both hosts).

The inference tests execute the exact production block against 15 detached
inputs per host, with no authority evaluation. They cover missing/malformed
summary, missing/false/true integrity, absent or duplicate directions, duplicate
pairs, foreign session/transfer, canceled/errored states and app terminal events.
Only separately supplied true integrity stays true. Completion reporting is
intentionally unchanged: duplicate or foreign status pairs can still affect
that existing reporting field, but cannot supply missing file integrity. These
controls do not assert an owner-bound pair proof or canonical acceptance.

Retention tests preserve five diagnostic rows, including duplicate, canceled,
errored and malformed evidence, at exact source origins `2,4,5,6,7`. Unrelated
and suffix event names are excluded; no authority is issued. Real integrations
also check exact raw-line equality across original/writer/semantic/retained
sources, primary physical lines, six fail-closed custody controls and unchanged
original views, then reverify the actual producer's file/source binding.

The pre-fix receipt `terminal-retention-red.trx` retains four expected failures:
both hosts excluded the terminal event and both incorrectly inferred integrity
from a status pair with no GUI summary. No final test expectation was weakened
to make the fix pass.

Strict Tuna-pair captures remain `FAIL_PROTOCOL_OR_INTEGRITY` with
`file_tuna_data_path_mismatch`, and the alternative shadow remains `unproven`:
`artifacts/publication-tuna-pairs-20260925/2b4db3e456984af5816b32574297c6ea/`
and `b878d961a8ac4c41b1def8ff2d77ca79/`.
No historical report was reclassified.

## Remaining boundary and preservation

This establishes source retention, not cross-process ownership of measured file
integrity. The producer still owns its private transfer/file capture; primary
writer membership remains unverified in this small fixture. No JSON observation
is accepted as a capability. The next step is the bounded owner-handoff design
and verification, preserving the same source/writer/primary attempt. Canonical
scope, complete five-route cycle and independent-failure inventory remain
separate requirements. This is not live-256 readiness.

Production edits are one catalog entry and removal of one inference assignment
(replaced by an explanatory comment). Reversing these scoped hunks reproduces
both baseline file hashes, preserving earlier catalog changes. All unrelated
dirty files and historical artifacts remain unchanged. `git diff --check` and
touched-file whitespace checks pass. The verification seal is
`artifacts/terminal-retention-fix-20260925/FINAL-VERIFICATION.json`.
No portable build, live run, full matrix, commit or push was performed.
