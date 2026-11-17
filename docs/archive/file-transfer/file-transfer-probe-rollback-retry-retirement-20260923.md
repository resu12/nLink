# Post-fallback probe rollback: exact retry-install retirement

## Outcome

The joined two-service/two-transport EOF regression now reaches a second,
independent coordinator-authorized install and real DispatchObserved settlement
for both `sidecar_tuna_stream_eof` and `listener_terminal_tuna_stream_eof`.
The final selected-test audit contains 41 distinct tests, all latest-passing.
This is offline contract evidence, not live acceptance or a complete Tuna route cycle.

Branch: `v0.7.0`; HEAD: `4d96cfb1a62481eeb21c6220a5214fd773d52d48`.
Changes remain uncommitted, alongside the preceding terminal-compensation analyzer fix.
No push, portable build, live run, full matrix, or historical campaign re-analysis
was performed in this milestone.

Artifacts: `artifacts/post-fallback-retry-install-20260923/`.
The preceding live campaign remains FAIL/proof_incomplete.

## Reproduction and root cause

The latest live runtime witness and original hypothesis are recorded in
`file-transfer-retry-confirmation-terminal-20260923.md`.
The new joined fixture continues beyond the real first offer, paired release,
installed authority, DispatchObserved settlement, and precommit-probe EOF.
It forwards actual peer yield frames for the next retry; it does not fabricate
the successor's peer proof or retry contract.

`joined-red-2.trx` reproduces the generation-2 independent confirmation rejection
in both EOF cases: `retry_dispatch_install_confirmation_receipt_mismatch`.
The coordinator had superseded the consumed pair, but the transport still retained
the old committed install. Its strict predecessor comparison correctly rejected
an independent authority with no predecessor.

Retiring the install alone admitted confirmation but exposed the retained physical
send scope (`joined-green-2.trx`): the new dispatch was held with
`predecessor_physical_send_scope_not_drained`. The accepted exact retirement
command now also cancels that old scope. Normal completion drains its children;
the existing drain gate is unchanged. `joined-green-3.trx` then passes both cases.
No callback, observation, or adapter dictionary was promoted to authority.

## Bounded implementation

Order: accepted route rollback, pending-probe retirement, coordinator-issued
FullScope retirement of the exact settled install, then paired-yield supersession.

The new reason `retry_dispatch_post_fallback_probe_rollback` requires an outbound,
nonterminal post-Tuna fallback source with its current publication, no pending
probe or active retry, and the exact DispatchObserved terminal install. The current
consumed pair, reservation, install, and applied rolled-back transition must agree.
Both coordinator preparation and commit validate this proof. The adapter preserves
its existing identity, generation, receipt, request, alias, and settlement checks.

Only successful application captures and cancels a physical scope whose complete
install receipt matches the command. Cancellation occurs outside the adapter lock.
Duplicate/rejected commands do not enter that effect path. Confirmation receipt
checks, route/protocol invariants, retry timing, throughput, and queue policies are
unchanged. A staging rejection leaves the old pair consumed and emits no new retry.

## Verification

- Both EOF variants: exact old retirement, independent generation-2 install,
  real offer/dispatch, unchanged fallback publication.
- Rejected staging generation on both variants: accepted rollback remains, but
  the old pair stays consumed and the install is retained; handoff reports rejection.
- Seven stale binding cases and duplicate EOF remain neutral.
- Old command and EOF replay after the new dispatch cannot retire/cancel its install.
- Twelve changed coordinator-proof cases reject at both prepare and commit;
  rejected/altered receipts are neutral and exact duplicate commit is idempotent.
- Real producer retirement snapshots pass full/compact evaluation on PowerShell
  5 and 7, with five missing-effect/history controls. The isolated two-transition
  slice is Shadow evidence, not a GUI route-authority claim.
- Existing scope-invalidation, sequential-scope, stage-expiry, stale-receipt,
  physical-send cancellation/drain, and joined success/stalled-repair regressions pass.

All intermediate failures and corrections are retained. Initial compile errors,
an over-strong negative-test handoff expectation, and evidence-fixture setup errors
are not represented as passing runs. See `test-audit.json` for latest outcomes.

## Stale analyzer-test assertion found during regression

Both `FileTransferOps_PendingScopeInvalidationUsesExactSharedValidation` variants
failed at `GUI rejected valid preparation`. The same original script and fixture
inputs failed with both HEAD-exported and working-tree analyzer/GUI tools.
`head-evidence-comparison.json` and `pending-evidence-diagnosis.json` retain that
comparison. This is a committed-tools comparison, not a separate HEAD application build.

Artifact validity was passing; route consistency correctly rejected the claimed
authoritative stream because it contained no route publications. The test had
incorrectly equated its isolated retirement-contract validity with GUI route validity.
The assertion now requires exactly that missing-publication rejection, including
full/compact route parity and the GUI's exact selected boundary. All existing
receipt, history, effect, and malformed-input checks remain. Both tests pass.
No production analyzer or GUI rule was weakened for this correction.

## Next gate

1. Review/checkpoint the accumulated analyzer and runtime changes locally.
2. Rebuild the portable package from that checkpoint.
3. Run strict escalated global/scenario preflights, then only
   `second-transfer-after-reactivation` once. Stop on a failed preflight.
4. Require actual coordinator route-cycle/second-transfer proof. SHA OK or fallback
   completion alone does not qualify. The original physical EOF and later sender
   disconnect are not claimed fixed by this offline milestone.
5. Resume the full GUI matrix only after the targeted correctness gate passes.
