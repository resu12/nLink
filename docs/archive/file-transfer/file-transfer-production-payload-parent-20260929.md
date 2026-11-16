# Production wrapper-to-parent payload handoff — 2026-09-29

## First missing connection

The normal loader already supported the original GUI-to-wrapper pair and its
terminal native-writer association. The next edge, wrapper-to-parent payload
custody, remained test-only. Both normal-loader regression cases reproduced
`Production parent payload handoff missing` before this implementation.

The predecessor is `production-payload-writers-20260929/FINAL-VERIFICATION.json`,
SHA256 `d86016ea983982e420874c15acaf40e5b6123d1380f1f1b09981ea74dcce8ac2`.
All 14,260 pins matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the baseline had 666
dirty entries. Before-images preserve ten existing text files and five pinned
build outputs. No predecessor report or seal is changed.

## Bounded implementation

- The normal loader now includes `LocalGuiWriterCanonicalPayloadParent.cs`
  and `LocalPayloadParentChannel.cs`. The implementation is moved from the
  fixture, which retains only diagnostics and deliberate test faults.
- `TryStartPayloadParent` accepts only the original relay launch reports and
  current original registration. The launch retains those exact reports;
  readiness and reports alone do not establish accepted custody.
- `TryOfferPayloadPair` takes the original writer-bound wrapper pair and exact
  accepted completion, with no public fault parameter. The original handoff,
  attempt, writer manifests, terminal sequences, and current file pins must
  match. The offer and parent pull are one-shot.
- The existing bounded channel is unchanged except for its type name: an
  8192-byte maximum packet, original 32-byte binding, packet digest, reserved
  byte and tail checks, monotonic pull/commit states, and cancellation. Channel
  commit remains provisional, not route, writer, or runtime authority.
- The parent duplicates exactly two read-only native payload handles from the
  original wrapper process. It preserves native file identities and the
  previously verified equal 256-MiB payload hashes. Terminal take requires
  normal wrapper exit, completed output capture, current committed channel,
  current files, the original accepted native relay result, exact completion
  hash, and matching writer facts.
- Acceptance invokes the original relay take once and detaches the same native
  custody once. The original cumulative five-second active-work clock is reused;
  no allowance, deadline, retry, or acceptance threshold is changed.
- Native custody cancellation now retires the payload pair in normal builds as
  well as the fixture. Failed terminal association disposes both pair and
  native custody; disposing an accepted pair does not dispose its separate
  native writer owner. Rejected copied objects cannot retire originals.
- Diagnostic partial hooks observe existing results without reevaluating
  guards. They and the test-only analyzer linkage are absent from the normal
  assembly. Analyzer payload forwarding and live ownership remain disabled.

All live-proof, transfer-integrity, and publication-completion flags remain
false. No coordinator, Core, transport, route invariant, progress display,
throughput policy, or acceptance rule is changed.

## Verification

The stabilized build has 339 warnings and zero errors. The first lane passed
8/8: both normal loaders, both valid full offline handoffs, both normal-loader
channel-control cases, and the two exact guard-order/single-evaluation checks.

The focused failure lane passed 9/9: cancellation before commit, foreign writer
facts, rejected native scope, disposed native custody, invalid native wrapper
state, native transfer cancellation, cumulative budget exhaustion, nonzero
wrapper exit, and killed wrapper. These fixtures also prove one-shot behavior,
copied-input rejection, continuous file protection, and independent handle
release on native retirement.

The affected compatibility lane passed 12/12: native-writer and wrapper normal
APIs on both hosts, both valid native-writer associations, analyzer API exclusion
on both hosts, the retained canonical-source analyzer consumer, and the three
native acceptance/retirement source-contract checks. The latter preserve the
pre-diagnostics source hash after reversing only the reviewed pull-order change
and the new normal payload-lifetime notification. The retained analyzer still
uses its original three evaluations and one native read and stops before route
acceptance.

Combined result: **29 distinct passing offline tests**. Both intentional
missing-API red results are retained and required to resolve to passing cases.
After removing one newly introduced test-style warning, the single affected
lifetime assertion was rebuilt and passed again. No aggregate suite or full GUI
matrix was run.

The verifier checks all predecessor pins, exact dirty-state preservation,
before-images, build and test outcomes, script syntax, whitespace, the unchanged
live entry, and recorded child-process cleanup. Two new production files and
this report are the only new non-ignored paths; no fixture is deleted.

## Next gate

Connect the original parent payload pair to the retained primary analyzer
without reconstructing ownership or adding an authority evaluation. Live
execution and acceptance remain gated.

The live entry remains `Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`.
There is no portable rebuild, live NKN, live-256, full desktop GUI matrix,
commit, or push in this phase. A second coordinator-published `file_tuna_v4`
route remains mandatory for the live route-cycle contract.
