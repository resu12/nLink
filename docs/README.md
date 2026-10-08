# nLink Docs

Start here for current repo guidance. `0.7.0` is a release candidate, not yet published. Its current qualification status and retained evidence are summarized in the [release runbook](ReleaseRunbook.md#070-candidate-evidence-2026-10-08); candidate notes live in [releases/0.7.0.md](releases/0.7.0.md).

## Current Operator Guides

- [`docs/test-lanes.md`](test-lanes.md): stable test lane matrix after the Track C project split.
- [`docs/supportability.md`](supportability.md): support evidence checklist for bug reports, diagnostics, hang reports, logs, and screenshare evidence.
- [`docs/screenshare-implementation.md`](screenshare-implementation.md): current H.264 screen-share capture, transport, decode, recovery, and diagnostics design.
- [`docs/screenshare-operability.md`](screenshare-operability.md): screenshare-specific operator model and `ScreenShare-Ops.ps1` entry point.
- [`docs/file-transfer-implementation.md`](file-transfer-implementation.md): current route-aware single-file transfer protocol, V4 regular/Tuna routes, controlled V6 fallback, lifecycle safety, and diagnostics design.
- [`docs/file-transfer-operability.md`](file-transfer-operability.md): start here for file transfer/Tuna usage, support capture, operator triage, and the distinction between correctness and performance.
- [`docs/nkn-tuna-implementation.md`](nkn-tuna-implementation.md): experimental NKN Tuna wallet, sidecar, session binding, runtime unlock, caps, and fallback design.
- [`docs/file-transfer-soak.md`](file-transfer-soak.md): targeted local/live commands and the strict six-scenario Phase 5 release gate.
- [`docs/file-transfer-stabilization-protocol.md`](file-transfer-stabilization-protocol.md): coordinator-authority invariants and bounded remediation rules.
- [`docs/screenshare-soak.md`](screenshare-soak.md): technical reference for local screenshare soak output and retained Track B evidence.
- [`docs/screenshare-stabilization-protocol.md`](screenshare-stabilization-protocol.md): promotion rules for planned screenshare runtime behavior changes.

## Release And Validation

- [`docs/ReleaseRunbook.md`](ReleaseRunbook.md): canonical release process, package provenance, and current candidate evidence.
- [`docs/build-test-lock-avoidance.md`](build-test-lock-avoidance.md): Windows-safe build/test command shapes for avoiding generated-output locks during repeated manual runs.
- [`docs/release/rc-validation-checklist.md`](release/rc-validation-checklist.md): active RC validation checklist.
- [`docs/release/rc-guardrails.md`](release/rc-guardrails.md): RC guardrail workflow rules.
- [`docs/BETA_HARDENING_EXTRAS.md`](BETA_HARDENING_EXTRAS.md): optional Windows hardening extras.
- [`docs/RELEASE_NOTES_TEMPLATE.md`](RELEASE_NOTES_TEMPLATE.md): release notes template.

## Product And QA References

- [`docs/KnownIssues.md`](KnownIssues.md): current support and limitation notes.
- [`docs/BetaUxGate.md`](BetaUxGate.md): UX invariants for beta/release readiness.
- [`docs/FIRST-RUN-PERFORMANCE.md`](FIRST-RUN-PERFORMANCE.md): first-run performance expectations.
- [`docs/SessionUxContract.md`](SessionUxContract.md): session UI contract.

## Historical And Experimental References

- `docs/releases/**`: published release records plus explicitly marked candidate drafts. Old release notes are not current operating instructions.
- Dated file-transfer RCA, result, review, and contract documents are retained as evidence and decision history. They do not automatically describe current code or impose a new release prerequisite; check the covered source, contract, and payload before reusing their conclusions. Current gates are in the runbook and RC checklist, with approved test dispositions in [test-lanes.md](test-lanes.md).
- [`docs/tuna-poc-runbook.md`](tuna-poc-runbook.md): standalone synthetic benchmark, not authenticated nLink payload or release qualification.
- `docs/images/**`: screenshots used by README and release docs.
