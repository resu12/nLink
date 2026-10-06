# Stale-rejection feasibility: closed with classifier no-go

The bounded design review was reopened on 2026-09-13; see
[the current evidence-contract review](file-transfer-security-provenance-contract-review.md).
This historical no-go and its unproved production boundaries remain unchanged;
no classifier or acceptance cutover has been enabled.

Date: 2026-09-10. Baseline: `d408e58c4c7e97cfdec451b663b55995e83ace37`.
Scope: initial 30-active-minute feasibility slice, separately approved test-only
follow-up and read-only binding review, then scoped local closeout; one agent.
Contract: [proven stale rejection proposal](<file-transfer-proven-stale-rejection-contract-proposal.md>).

## Final binding-review decision

**No-go for classifier implementation; park this experiment.** The candidate
proof link below is useful but does not meet the complete evidence contract.
The separately approved read-only review found three remaining boundaries:

- Security-session incarnation is unproved. Key installation resets secure
  sequences and replay windows, while the log writer ID is process-scoped and
  unchanged by that reset. A new Coordinator instance in the fixture does not
  prove uniqueness across key installations within a process. This is missing
  evidence, not a demonstrated runtime collision or security vulnerability.
  [Key installation](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L6173>),
  [Process writer identity](<../src/nLink.Core/Logging/LocalOperationalLog.cs#L16>).
- The fixture compares supplied writer labels, not ownership validated against
  real retained streams. Existing writer/run checks provide useful primitives,
  but this candidate join has not been integrated with or proved by them.
  [Fixture labels](<../tests/nLink.SmokeTests.Core/FileTransferCoordinatorTests.PairedDataPathProof.cs#L64>),
  [Writer evidence checks](<../tools/GuiSmokeLogLedger.ps1#L329>).
- Existing full/compact parity checks cover local Snapshot and peer-proof
  bindings, not the proposed cross-writer projection or its immutable handoff.
  Primary-preservation controls for that new handoff also remain unproved.
  [Current parity comparison](<../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1#L16342>).

The binding review changed no files and ran no tests. The subsequent approved
closeout records this decision and retains only the paired-proof fixture, its
default-preserving test-helper change and these two documents in the local commit.
No production implementation, validator or acceptance change, push, report or
live run is part of closeout. The passing test receipts below are prior runs,
not newly rerun checks. Unrelated workspace edits remain outside the commit.

## Subsequent test-only paired-proof slice

The user separately approved a 20-active-minute, test-only follow-up. Its result
is **a demonstrated candidate link, not approval of the classifier**. The
earlier no-go below remains applicable to the complete validated handoff.

Added a [four-case paired Coordinator fixture](<../tests/nLink.SmokeTests.Core/FileTransferCoordinatorTests.PairedDataPathProof.cs#L8>).
It captures continuous Coordinator Snapshot generations/hashes from initial
Regular through first Tuna and fallback for both roles. It creates each yield
frame with the unmodified service factory, serializes/deserializes it with the
production frame codec, and applies the opposite endpoint's actual frame via
the existing service peer-receipt adapter and Coordinator. The local receipt
uses the service's existing frame identity/fingerprint functions. The resulting
paired receipt is used unchanged in the receiver's second-Tuna transition;
the helper's old synthetic pairing step is explicitly skipped only for this
fixture. Both receipt publication scopes are asserted.

The service's existing Snapshot privacy projection preserves matching local-
proof and peer-proof identities/fingerprints. A fixture-only join also checks
direction, scope, original source-publication history/identity, yield authority
and generation, validated local/peer receipt markers and expected writer label.
The exact case matches. Wrong writer, an actually reserialized frame with a
changed sent timestamp, and a new source Coordinator with reused session,
transfer and yield generation do not match the original history.

Important limits: writer labels are fixture provenance, not independently
captured production log streams. Frame exchange and local send receipt are
controlled test inputs, not physical transport delivery. This does not test
secure-envelope payload identity, key-reset/session-incarnation exclusion,
actual bulk-send admission, emitted-stream completeness/authority evaluation,
full/compact projection parity, or primary-result preservation. The experimental
join is not a production validator and must not be copied into one as approved
policy. Additional ambiguity/ABA controls remain part of any binding review.

Verification: initial focused 4/4, directly affected unchanged-helper regression
3/3, final focused 4/4 after publication-scope assertions. Receipts are under
[paired-data-path-proof](<../artifacts/validation/consolidation-bounded-20260907/paired-data-path-proof/paired-data-path-proof-final.trx>).
The final rebuild first stopped in Avalonia's telemetry target because its
AppData log was not writable; retrying with `UsedAvaloniaProducts` empty skipped
only that telemetry target and passed compilation/tests. No runtime, validator,
acceptance, public schema or wire changes; no report, package, live run or commit.

The recommended binding review was subsequently completed; its final no-go
decision and remaining evidence limits are recorded above.

## Initial feasibility decision

**No-go for the proposed positive classifier.** The prerequisite inspection
reached the contract's stop condition before implementation: the inspected
completed validators and production-shaped transport fixture do not supply an
already-validated cross-writer publication-use/lineage link for this candidate.
This is an evidence-contract limitation, not an established runtime defect.

The missing edge is an exact causal reference connecting the old outbound
Regular publication to the inbound publication history that ends in second Tuna.
Two individually valid histories with matching route/epoch sequences are not
that reference. Neither matching timestamps nor equal generation numbers may
fill it. No new authenticated-identity field, detached carrier or hash can
create this missing causation proof.

## What the existing sources establish

- The transport fixture obtains real Coordinator-issued publication tokens for
  each role, holds an actual outbound envelope, injects it at the later receiver
  publication, and asserts unchanged transfer state and no delivery. However,
  it calls the route-cycle helper independently for inbound and outbound. That
  helper returns tokens, discards the intermediate Snapshot results and uses
  no-op evidence callbacks. It is not a validated two-writer evidence fixture.
  [Transport fixture](<../tests/nLink.SmokeTests.Core/NknFileTransferTransportTests.DataPathProvenance.cs#L80>),
  [Coordinator helper](<../tests/nLink.SmokeTests.Core/FileTransferCoordinatorTests.DataPathIngress.cs#L11>).
- Route consistency validates publication successors within a Coordinator,
  writer, session, transfer and direction. Its cross-direction check compares
  sequences of `liveRouteEpoch|route|protocolVersion`, ordered by local operation
  generation. It does not emit a causal link naming both writers' exact
  publication/receipt references.
  [Local history scope](<../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1#L15278>),
  [Cross-direction comparison](<../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1#L15703>).
- `__FallbackTailPeerProofBindings` preserves useful local pending/paired
  receipt and effect histories, including proof identities and fingerprints.
  The inspected builders emit one local writer/Coordinator per binding; they
  do not supply the required validated remote publication reference. Matching
  a local-proof identity to another writer's peer-proof identity would require
  a separately specified and verified join, not a claim that the current
  aggregate pass already certifies it. This review does not establish that
  such a join is impossible; it establishes that it was not proved here.
  [Pending binding](<../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1#L5162>),
  [Paired binding](<../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1#L12914>).
- The data-path and Snapshot session/transfer projections do use the same
  `fte1-` plus first 24 lowercase SHA-256 hex characters over the existing raw
  value. A token-domain mismatch is not the blocker identified in this slice.
  [Data-path projection](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L2815>),
  [Snapshot projection](<../src/nLink.Core/FileTransfer/SessionFileTransferService.CoordinatorSnapshots.cs#L3658>).

## Authenticated-identity design retained, not implemented

The proposed diagnostic binding is `ftauth1-` followed by the full lowercase
SHA-256 digest of this byte concatenation:

```text
UTF8("nLink/filetransfer/authenticated-identity/v1\0") || codec canonical AAD bytes
```

Here `\0` denotes one NUL byte, not backslash and digit zero. The codec AAD is
UTF-8, with invariant numeric formatting and U+001F separators, in the existing
order: version, numeric family, trimmed message type, session, sender, allocated
secure sequence, normalized request ID (empty when absent). A future private
codec-owned projection must reuse `CreateWire` and `BuildAuthenticatedData`,
not duplicate their normalization in transport or PowerShell. This design
is not an implemented/tested API or a replacement for the outer `ftenv1-` check.
[Canonical codec encoding](<../src/nLink.Core/SessionSecurity/SessionSecureEnvelopeCodec.cs#L243>).

Capture on send must use the exact metadata local that allocates the sequence
and is passed to encryption, carrying its resulting binding across the async
send. Capture on receive must use successfully decrypted metadata only after
the existing replay, transfer and session checks, before the rejection guard.
No mutable sequence-counter reread, plaintext/key logging, wire change or public
authority DTO extension is proposed.
[Send allocation](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L5617>),
[Validated receive boundary](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L4354>).

This tuple is not a payload-content hash. It additionally needs the contract's
validated session/sender/incarnation uniqueness before any positive result.
Key installation and secure-state reset can reset the sequence; no completed
incarnation/reset-exclusion proof was demonstrated in this slice. Do not derive
it from a Coordinator instance or a log writer merely because those are unique.
[Sequence reset](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L6173>).

## Initial handoff status and verification limits

The current carrier contains eligibility/input metadata, not the proposed
publication projection. The existing parent saves the completed primary before
starting its isolated companion. That ordering was inspected, not changed.
The worker currently serializes `diagnosticEligibility` in the same response
as the primary classification, so adding bounded projection work there does
not by itself prove the stronger primary-preservation requirement.
[Current worker response](<../tools/GuiSmoke-Windows.ps1#L31675>),
[Primary persistence before companion](<../tools/GuiSmoke-Windows.ps1#L37929>).

No production-shaped positive projection fixture, full/compact projection
parity, send/decrypted-receive binding test, reused-outer-ID negative, or
missing/tampered-projection preservation control ran. Those go criteria remain
unproved; existing tests are not relabeled as satisfying them. No production or
test code, build, test execution, report/evaluator, package, preflight, live run,
staging, commit or push belongs to this slice. Only this report, the proposal
status and the local accounting ledger changed. Unrelated work is preserved.

## Closeout and future scope

Park the stronger classifier and preserve current acceptance. Any further work
should address the original missing-Tuna-receive issue under a separately
approved scope, not implicitly extend this diagnostic contract. Reopening the
stronger classifier would first require a separately approved security-provenance
evidence contract and resolution of the writer, publication-use and handoff
boundaries above. This closeout authorizes none of that work.

Historical helper 9564 remains unresolved; quartet `25/25/18/7`, scalar `14`,
the exact Tuna-receive requirement and all primary acceptance rules are unchanged.
Standalone report allowance remains consumed (3); live campaign remains 6/6.
