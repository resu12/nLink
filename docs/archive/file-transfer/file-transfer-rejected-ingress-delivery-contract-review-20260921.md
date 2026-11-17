# Rejected ingress and later delivery: bounded contract review

Date: 2026-09-21. Reviewed HEAD:
`d96eeac0b9c1553c786b633adae91b4d1dd85be7`, plus the uncommitted test-only
adapter/Service reproductions. This review changes documentation only.

## Implementation follow-up

The user subsequently requested the next step. The reporting slice below is
implemented: one pure scalar formatter supplies the optional `Explanation`
field on retained matcher results and `explanation` on GUI companion records.
The parent accepts omission and rejects altered/inapplicable explanations
without changing the persisted primary. No new evidence join or authority
evaluation is performed. The formatter's only dependency is PowerShell itself;
its load is checked not to reset the retained parser's existing state.

Twelve selected tests pass, including 48 scalar formatter controls, the existing
24-case recovery matrix, full/compact/shadow parity, isolated subprocess and
additive-persistence controls, and fresh generated transport/Service evidence
on PowerShell 5.1 and pwsh. The new Service capture still has 96 passing Snapshot
boundaries and three original evaluations per host; its strict result remains
`missing_tuna_resend` despite the independently verified later disk write.
See `artifacts/rejected-ingress-explanation-20260921/RESULTS.md` for receipts.

This completes the bounded reporting implementation. Next is pre-commit review
of the accumulated test/reporting changes. It is not approval of the stronger
classifier, an acceptance exemption, a runtime change or another live run.
The original review and unchanged contract follow.

## Decision

Keep the exact-Tuna recovery contract unchanged. The new fixture demonstrates
that rejection before dispatch can coexist with independently authorized later
delivery to disk. It does not establish that a live missing-resend finding is a
false positive, or that the runtime must resend every rejected range over Tuna.

**Go for a small reporting-only clarification using existing matcher results.**
**No-go for a new combined stale-safety/durable-delivery classification.** The
parked `proven_stale_rejected` proposal remains disabled. Neither a new runtime
producer nor an acceptance exemption is part of this next slice.

The simpler next step is to explain the existing result, not introduce another
correlator. The existing diagnostic already separates `RejectionChainMatched`
and `ReceiveDisposition` from `Recovered`, `Reason` and `Variant`.

## Evidence and limits

| Question | Current evidence | Permitted conclusion |
| --- | --- | --- |
| Did the held packet affect this receiver? | Genuine adapter rejection plus unchanged Service state, progress, hints, control sends and file hash in the deterministic fixture | Scoped rejection behavior is tested; not general live stale-safety certification |
| Was the rejected packet the held old encrypted packet? | Captured outer/security payload references and controlled replay in the fixture | Exact old-envelope correspondence in this fixture; an outer ID alone is insufficient |
| Was later delivery independently authorized? | Real receiver Service final-fallback committed/applied disposition, new owner and publication 5, protocol 6 | The fixture follows a separate authorized transition, not authority from rejection |
| Were the same bytes written? | Test compares 43,008 bytes, flushes to disk and reopens the destination range | Test-only durable range proof; not a production receipt or whole-file completion |
| Was the earlier exact-Tuna obligation met? | Original primary still reports `missing_tuna_resend` | No; later fallback cannot retroactively supply the missing earlier Tuna evidence |
| Does the exported local publication proof establish peer lineage? | Current carrier requires `scope=local_publication_forwarding` and `peerLineageStatus=not_evaluated` | No; local reference validation must not be promoted to a cross-writer safety verdict |

The fresh fallback envelope differs from the rejected envelope. Their shared
session/transfer, establishment, range and byte count do not by themselves prove
equal plaintext. The equality assertion comes from the controlled fixture and
its reopened disk bytes, not from comparing encrypted-payload fingerprints.

The Service test calls the production V6 handler through the already-bound
session wrapper's received frame. It does not run two complete production
Services or the complete receiver pump. Fixture bootstrap/control setup and any
repair-alias relay remain explicit limitations. `service-durable-progress.json`
is test output, not an issuer-backed authority/durability token.

The inspected production V6 sparse path writes the accepted bytes and then
commits progress. Its frontier observations do not carry the fixture's reopened
range hash. Do not infer flush-to-disk, byte equality or candidate causation from
frontier/byte counters, an adapter receive, or an aggregate Snapshot pass.

## Minimal reporting contract for the next implementation

Add only an optional, fixed explanation to the existing non-authority diagnostic
result. Reuse the already-computed matcher result; do not rescan inputs, join
later fallback events, read destination files, evaluate Snapshot authority,
export additional private rows, or consult the fixture progress JSON.

Emit an explanation only for an internally consistent existing result:
`Recovered=false`, `RejectionChainMatched=true`,
`ReceiveDisposition=rejected_before_dispatch`, and one of these exact pairs:

- `missing_tuna_resend` /
  `second_tuna_rejected_regular_ingress_exact_tuna_resend_missing`:
  "The rejection chain matched, but the required pre-rejection exact Tuna send
  was not found. Later fallback delivery, if present, is a separate outcome and
  does not satisfy this recovery contract. Stale safety and durable delivery are
  not established by this diagnostic."
- `missing_exact_tuna_receive` /
  `second_tuna_rejected_regular_ingress_exact_tuna_receive_missing`:
  the same explanation with "send" replaced by "receive".

Here "rejection chain matched" is deliberately the legacy matcher's statement,
not a claim that every record explicitly captured a disposition or that security
incarnation, all authority effects and cross-writer causation were adjudicated.
Unknown, incomplete or contradictory result tuples receive no explanation.
Do not label them safe, unsafe, delivered, lost or recovered.

Use one shared formatting path for retained candidate diagnostics and the
isolated GUI companion. Keep the field optional. Fixed text is capped at 768
characters per existing record; no additional records, identities, counters,
status enums, mandatory event families, input admission limits or budget changes.
The formatter accepts scalar result fields only and performs no I/O.

Preserve all existing fields exactly, including reason/variant, raw/distinct/
recovered/unresolved counts, legacy scalar, candidate membership and selection,
source identities/locations, primary decisions, operator/GUI acceptance and
original terminal receipts. There is no combined "successful rejection plus
delivery" count. Optional GUI enrichment remains after primary persistence;
failure, timeout or malformed enrichment cannot replace the saved primary.

## Focused verification for that slice

1. Extend the existing second-Tuna diagnostic matrix for both exact reason/
   variant pairs, recovered positives, missing/ambiguous rejection companions
   and contradictory formatter inputs. Assert unchanged legacy fields/counts.
2. Keep the existing later-fallback-only and later-fallback-after-Tuna-send
   controls unresolved. A SHA/progress/fallback observation must not produce
   a recovery or a positive delivery/safety statement.
3. Extend full/fresh-compact/shadow parity and isolated-companion preservation
   tests on PowerShell 5.1 and pwsh. Omit/remove the optional field and compare
   the pre-existing semantic result. Preserve input binding and primary-before-
   enrichment failure behavior; do not rerun historical whole reports.
4. Reuse the Service reproduction's saved receipts as review evidence. Rerun
   that integration only if the implementation changes a diagnostic path it
   exercises. No new aggregate suite is required for fixed text formatting.

## Stronger claims and live-test boundary

A future combined finding would need an exact validated candidate-to-peer
publication history, security-installation/retirement exclusion, applicable
contradiction checks, and a production payload-to-durable-write binding. The
current local carrier and fixture assertions do not supply that complete export.
Do not reconstruct it from timestamps, equal generations or missing effect logs.
Any such producer/consumer work needs its own reviewed bounded contract; this
review does not revive the older large classifier proposal by implication.

The reporting clarification is not a live-readiness gate and will not make the
live-256 test pass. Do not launch another live test merely to seek a different
result under the unchanged predicate. Independent full-matrix failures remain
separate work. Any later acceptance change requires an explicit policy decision;
any runtime fix requires current relevant evidence and its first missing edge.

## Verification of this review

Read-only checks reconfirmed the fresh merged input SHA-256
`198b48fdd4e426a347f0113eecc70a853c8ea9bfe0b3739a3e3064db0d8ce749`.
Both original primary receipts remain completed/exit 0 with unchanged input;
all 11 bound output hashes per host match. Both Snapshot summaries have 96
boundaries, complete streams, passing artifact evidence and passing parity.
Exit 0 means analysis completion, not acceptance. The prior TRX receipts retain
1 focused plus 27 affected passes, with zero failures or aborted cases.
These are verification of saved evidence, not new test or analyzer runs.

Source anchors: `tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1`,
`tools/FileTransferOps/GuiRejectedIngressShadow.ps1`,
`tools/FileTransferOps/GuiDataPathDiagnosticCompanion.ps1`,
`tools/FileTransferOps/LocalPublicationEvidenceCarrier.ps1`,
`src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV6.cs`,
and `tests/nLink.SmokeTests.Core/SessionFileTransferSecondTunaDurableFallbackTests.cs`.
The fixture details remain in
[the Service proof](file-transfer-second-tuna-durable-fallback-proof-20260921.md).
No runtime/analyzer/test code, acceptance rule, package, live run, historical
artifact, commit or push changed in this review.
