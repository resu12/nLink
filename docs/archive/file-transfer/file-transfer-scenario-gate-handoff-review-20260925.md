# Alternative acceptance: scenario-gate handoff review

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus existing uncommitted work.
Review only: no implementation or acceptance adoption in this step.

## Decision

The existing local publication receipt is not a whole-scenario acceptance
receipt. Do not extend its meaning or build a generic replacement channel first.
The smallest useful next implementation is a **test-only primary-local payload
proof**, exercised by genuine send/receive pairs during both Tuna publications
in the existing two-process offline fixture. That is one bounded milestone,
not a complete alternative-acceptance implementation.

Scenario scope and whole-transfer integrity need evidence from their actual
owners. Until that handoff and the remaining independent gates are implemented
and verified, the integrated alternative shadow must remain `unproven`.
The strict matcher, `Recovered`, primary verdict and historical results stay
unchanged. A future acceptance cutover still requires separate approval.

## Evidence and ownership

| Obligation | Current owner/evidence | Missing proof or limit |
| --- | --- | --- |
| Recorded rejection and candidate coverage | Original primary custody and already validated diagnostic chain | The bounded shadow covers these two gates only. |
| Route publications and payload observations | Original primary full/compact inputs and completed Coordinator evaluations | Need exact per-activation payload pairing and current-publication interval checks; aggregate route labels are insufficient. |
| Canonical scenario scope | Parent's actual request and runtime-manifest checks | The current offline parent requests `2B`, not 268,435,456 bytes. A scenario name cannot substitute for those inputs. |
| Completion and whole-file integrity | Actual GUI transfer states, expected/received hashes and saved size | Current test worker writes a controlled success summary; it does not complete or hash the requested transfer. |
| Other unresolved failures | Original primary proof collection and independent verdict factors | Removing one selected failure or subtracting a count cannot establish this gate. |

The completed shadow capture
`artifacts/publication-alternative-acceptance-shadow-20260925/66a40e4297204b88822f45254b0d20ea/whole-wrapper-7-write`
contains one Tuna send, no Tuna receive and no transfer-terminal event. Its two
writers each record Regular-v4, Tuna-v4, fallback-v6 and second Tuna-v4
publications; there is no final fallback publication in this capture. The
analysis receipt's `completed` status means analysis completed, not transfer
completed. These are read-only inventory findings, not a new authority
evaluation or historical reclassification.

`LocalPrimaryPublicationCompletion.cs:76` explicitly emits
`nlink-local-publication-forwarding-v2`, scoped to
`local_publication_forwarding`, with peer lineage `not_evaluated`.
`LocalPrimaryPublicationChannel.cs:28` explicitly separates its transport
receipt from the parent's diagnostic/terminal acceptance checks. Its private
issuer, source and worker binding are useful safeguards, but do not supply
missing scenario semantics. The eleven artifacts pinned by the existing local
adoption receipt do not include the GUI summary or destination file.

The compact analyzer's `guiIntegrityOk` is also insufficient as a standalone
whole-file hash proof: it reads the GUI summary and can infer that Boolean from
paired completion events (`AnalyzerOrchestration.ps1:3059,3116`). The actual GUI
path compares both terminal states, saved size and actual versus expected hash
(`GuiSmoke-Windows.ps1:37655`). This review does not change either path.

## Next bounded implementation contract

Keep the work inside the existing default-off test observer and its original
primary custody. No production code, wire format, acceptance predicate or
cross-process receipt change is needed for this milestone.

1. Add a first-Tuna checkpoint to the existing fixture, before its real
   Coordinator fallback transition. Its sender currently publishes first Tuna
   and fallback without a payload checkpoint; its receiver exposes only an
   `afterSecondTuna` callback. Exchange one actual encoded/authenticated payload
   through the existing FakeNKN transport while both first publications are
   current. Add an equivalent successful pair under second Tuna.
2. Evaluate each pair from already-owned original evidence. Require matching
   session/transfer, authenticated payload identity, range, byte count and chunk
   count. Bind each side to its own Coordinator-issued publication and applied
   forwarding receipt, with the complete local route/runtime tuple. Do not
   equate independent peers' generations or use wall-clock/merged-log ordering
   as a causal clock.
3. Require local publication and forwarding before use, and no supersession or
   terminal boundary before that use. Check the relevant full and compact
   authority views independently and require parity. A historical forwarded
   publication, queued send or an unmatched receive is not a successful pair.
4. Keep the independent payload pair separate from the rejected Regular range.
   Use a different, internally exact range in the positive fixture so the
   original strict candidate remains unresolved. Do not manufacture an exact
   replacement, force a runtime retry, rewrite records or treat a rejection as
   authority. Default behavior of existing strict fixtures must be preserved.
5. Report only the two local payload-gate results in the separate test shadow.
   Do not mark canonical scope, full cycle, terminal integrity or remaining
   failures as passed. The overall integrated result remains `unproven`.

Use existing native input positions for local diagnostics without reopening
inputs or expanding the primary witness selection/export. Preserve three
original evaluations, the 4,096-record, 256-boundary/view,
64-operation/boundary, 64-witness and cumulative 20-second limits. If the owned
views lack a required authenticated identity, publication interval or source
binding, report that exact gap; do not introduce a runtime producer or increase
limits under this contract.

Likely edit scope: the two-writer ingress fixture, second-Tuna Service fixture,
test-only shadow helper/probe and their focused tests. Reuse the existing
Coordinator history and transport helpers; no new general-purpose framework.

Verification must cover a genuine pair on both activations plus omitted receive,
wrong range/identity, swapped activation, superseded publication, missing receipt,
parity disagreement and incomplete/budget-exhausted evidence. Detached negative
controls exercise predicates; the positive binding must come from actual fixture
execution. Run the affected integration on PowerShell 5.1 and 7, preserve
default-off behavior and original strict results, and verify original custody,
views and reports unchanged. No aggregate matrix or live test is needed here.

## Boundary for a later whole-scenario handoff

The parent must retain ownership of actual scenario arguments, attempt/run IDs,
payload size, direction, fault, payer, toggle sequence and executable-manifest
checks. Any later versioned gate handoff must bind the same original attempt,
writer, immutable source and completed primary invocations. The local publication
profile must not be relabeled as that handoff; copied JSON or a test-authored
all-pass gate object is not an owner-issued result.

Terminal integrity must bind the actual completed inbound/outbound states,
expected payload size/hash and actual destination size/hash for that attempt.
The existing durable-fallback fixture starts with a seeded frontier, writes and
reopens a 42 KiB range, asserts nonterminal state and emits
`WholeTransferCompleted=false`. It is useful range-durability evidence, not an
end-to-end positive; changing a summary Boolean cannot close this gap.

The remaining-failures gate must account for every original distinct eligible
candidate and every independent primary failure factor. In particular preserve
hard failures, route/protocol checks, liveness timeouts, warning policy,
incomplete proofs and artifact/parity failures. The primary's aggregate verdict
and first-failure selection are lossy summaries, not a sufficient gate handoff
(`AnalyzerOrchestration.ps1:6175-6363`). Never subtract the admitted count and
declare the rest passed.

A small offline fixture may validate these local semantics but cannot be called
a positive canonical 256 MiB scenario. A full positive needs the actual scoped
payload, complete route cycle and genuine terminal/integrity evidence; fitting
that evidence within the existing bounds has not yet been demonstrated. Keep
that unresolved requirement explicit instead of quietly weakening scope.

## Review verification

Read source and retained artifacts only. No tests were rerun because no tested
code changed; the five accepted focused outcomes and their development/final
receipt distinctions remain as documented in
`file-transfer-alternative-acceptance-shadow-20260925.md`.
All 80 pre-existing dirty-file contents and the preceding seal's 49 pins are
preserved. `git diff --check` passes and HEAD is unchanged. The review adds only
this document and an ignored verification seal at
`artifacts/scenario-gate-handoff-review-20260925/FINAL-VERIFICATION.json`.
No runtime, classifier, acceptance, historical verdict, live run, commit or push
changes were made.
