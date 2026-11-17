# Correlated bootstrap analyzer ownership

Implemented locally on 2026-09-12 against `e0650051`; not live-validated.

Pre-commit review identified a source-hold invalidation gap, now corrected locally.
A completed exchange can retain its Completed state after its exact source hold is
invalidated. The selector now checks that hold separately instead of treating
Completed alone as current authority. The real Coordinator regression and review
record are retained in `artifacts/bootstrap-ownership-fix-20260912/`.

## RCA

Attempt12's immutable final-off evidence contains a validated second inbound
correlated exchange completed before the legacy bootstrap wait. The analyzer's
snapshot augmentation nevertheless required a legacy bootstrap decision, which the
negotiated correlated path intentionally does not emit. This recreated one obsolete
obligation (`combined_v6_bulk_and_control_proof_lost_bootstrap_direction`).

The retained Ready trigger at line51789 and Coordinator completion at line50968
share the exact TunaV4 source: epoch3/runtime4/publication4/commit2/leg4/data-session1.
Both peers published final fallback before the wait at line51791. This is an
authority-model selection error, not evidence that runtime bootstrap direction was
lost. SHA success alone does not establish this conclusion.

## Bounded implementation

`Test-FileTransferFallbackV6BootstrapCorrelatedEpisodeConsumed` selects correlated
ownership only from the caller's complete, integrity-valid authoritative snapshot
evaluation and a correlation-capable catalog. It requires route-transition authority,
one exact writer/source/identity join, a valid Ready trigger, and an accepted,
state-applied Pending-to-Completed exchange after the pending marker and before the
first wait. Agreement, peer echo and retained-peer-frame checks remain strict.

Redacted identifiers require a unique validated writer/source owner. An ambiguous
older stream, source mismatch, new pending episode, invalidation or accepted
replacement/restoration cannot borrow completion. Rejected unrelated callbacks
remain diagnostic-only. Adapter pending/fallback counters are not equated with
Coordinator operation generations.

For initiation-capable catalogs, the exchange must bind to its accepted, drained
source hold by identity/generation, command, source boundary and frozen checkpoint.
The first subsequent accepted loss of that exact hold is sticky: restoring it
cannot borrow the old completion. Cleanup is harmless only when the same stream
already contains the exact Coordinator fallback prepare/commit/disposition chain
before that loss, bound to the exchange's source, retained peer frame and checkpoint,
with validated preserved receipts and a protocol6 fallback target. Later closure,
unrelated authority and observations cannot substitute. Correlation-only catalogs
keep their older contract; no source-hold requirement is retroactively invented.

The full analyzer, compact analyzer and shared GUI diagnostic receive their existing
snapshot evaluation explicitly. The legacy path remains the default when that input
is absent or not authoritative. This change retires only the obsolete diagnostic;
it does not mark a route recovered or waive publication, route-cycle, artifact,
receipt, terminal or integrity failures. No runtime, wire, catalog or policy changes.

## Verification and handoff

The production Coordinator/projector fixture reproduced the failure on PowerShell
5.1 and7 before the fix. The final dual-host regression passes, including44 negative
predicate/history controls per host, full/compact result parity, shared GUI selection,
and preservation of independent route/publication failures. Modeled adapter effects
are test inputs, not live acceptance evidence.

The affected legacy/peer-first/writer-mapping/GUI/successor-history lane passes15/15.
Together these cover17 distinct passing xUnit cases. The final new-branch hardening
reran only its two affected cases; the legacy lane was not repeated. Script parsing
and `git diff --check` pass.

The source-hold review correction has a separate final12/12 passing run: eight
ownership scenarios across PowerShell5.1/7, plus four directly affected source-hold
full/compact/GUI contract cases (each checks both hosts). The committed-cleanup
scenario adds22 negative predicate controls per host for missing/mismatched/late
hold or route authority, receipt validation and restoration. Additional controls
preserve rejected/unrelated hold neutrality and correlation-only catalog behavior.
The pre-fix red run failed only the two invalidated-before-commit cases; committed
fallback and post-commit cleanup remained passing. The review finding is resolved.

Detailed results and retained development TRX files are in
`artifacts/bootstrap-ownership-fix-20260912/RESULTS.md`.

Attempt12 remains FAIL; all127 frozen inputs and the retained log are unchanged.
Implementation and validation did not replay a report, rebuild a package, launch a
live run, push or deploy. The reviewed analyzer-only change is scoped for a local
commit, excluding unrelated frontier work. Any retained-report replay or live
validation requires a new explicit allowance: attempts12/reports5 remain consumed.
