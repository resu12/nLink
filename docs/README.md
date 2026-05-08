# nLink Docs

Start here when you need current repo guidance. Historical release notes live under `docs/releases/` and are preserved as release records, not active process docs.

## Current Operator Guides

- [`docs/test-lanes.md`](test-lanes.md): stable test lane matrix after the Track C project split.
- [`docs/supportability.md`](supportability.md): support evidence checklist for bug reports, diagnostics, hang reports, logs, and screenshare evidence.
- [`docs/screenshare-implementation.md`](screenshare-implementation.md): current H.264 screen-share capture, transport, decode, recovery, and diagnostics design.
- [`docs/screenshare-operability.md`](screenshare-operability.md): screenshare-specific operator model and `ScreenShare-Ops.ps1` entry point.
- [`docs/file-transfer-implementation.md`](file-transfer-implementation.md): current V5 single-file transfer protocol, data session, handoff recovery, lifecycle safety, and diagnostics design.
- [`docs/file-transfer-operability.md`](file-transfer-operability.md): file-transfer-specific operator model and `FileTransfer-Ops.ps1` entry point.
- [`docs/nkn-tuna-implementation.md`](nkn-tuna-implementation.md): experimental NKN Tuna wallet, sidecar, session binding, runtime unlock, caps, and fallback design.
- [`docs/screenshare-soak.md`](screenshare-soak.md): technical reference for local screenshare soak output and retained Track B evidence.
- [`docs/screenshare-stabilization-protocol.md`](screenshare-stabilization-protocol.md): promotion rules for planned screenshare runtime behavior changes.

## Release And Validation

- [`docs/RELEASING.md`](RELEASING.md): short release checklist.
- [`docs/ReleaseRunbook.md`](ReleaseRunbook.md): detailed version-neutral release runbook.
- [`docs/release/rc-validation-checklist.md`](release/rc-validation-checklist.md): active RC validation checklist.
- [`docs/release/rc-guardrails.md`](release/rc-guardrails.md): RC guardrail workflow rules.
- [`docs/BETA_HARDENING_EXTRAS.md`](BETA_HARDENING_EXTRAS.md): optional Windows hardening extras.
- [`docs/RELEASE_NOTES_TEMPLATE.md`](RELEASE_NOTES_TEMPLATE.md): release notes template.

## Product And QA References

- [`docs/KnownIssues.md`](KnownIssues.md): current support and limitation notes.
- [`docs/BetaUxGate.md`](BetaUxGate.md): UX invariants for beta/release readiness.
- [`docs/FIRST-RUN-PERFORMANCE.md`](FIRST-RUN-PERFORMANCE.md): first-run performance expectations.
- [`docs/SessionUxContract.md`](SessionUxContract.md): session UI contract.

## Historical Records

- `docs/releases/**`: release notes and GitHub release bodies for shipped versions.
- `docs/images/**`: screenshots used by README and release docs.
