# Reopened security-provenance contract review

Follow-up: the user subsequently approved evidence-only capture implementation.
See the [pinned capture contract](file-transfer-security-capture-contract.md).
The review below is the pre-implementation record; its classifier no-go,
publication-link requirements and unchanged acceptance decision still apply.

Date: 2026-09-13. Reviewed source: `7e61c7603b75218a4243d29c64179a765d43de97`.
The user approved reopening the bounded design review and continuation of
justified offline work, stopping before a new live 256 MiB test. This review
does not enable a classifier or change acceptance rules.

## Decision

Keep rejection safety, source provenance and delivery recovery separate.
Do not implement the parked `proven_stale_rejected` classifier from the current
records. A metadata-only authenticated-identity digest is insufficient across
unbound key installations; this is now demonstrated by two executable tests,
not only identified as an inspection concern.

Recommended next implementation is **evidence-only security-context capture**,
followed by exact publication-use/lineage validation. That touches the private
key-install/encryption/decryption path and must have an explicit, reviewed
capture contract before coding. No change to cryptography, keys, replay policy,
wire formats, public authority DTOs or Coordinator effects is proposed.

The classifier and any later acceptance change remain separate decisions.
Do not solve the missing delivery evidence by weakening the current matcher or
changing scenario timing. The latest run stays FAIL.

## What the latest capture establishes

The finalized `b389db81-37fd-47c3-ba60-c5d610747418` run has 29 unresolved
second-Tuna receives, all with an exact rejection chain and reason
`missing_tuna_resend`. First: retained line 20897, receiver writer sequence 9633,
range `[5004,5007)`. Its exact Tuna resend and receive are absent; later
fallback delivery is a different proof. See
[the completed RCA](../artifacts/rejected-ingress-20897-rca-20260913/RESULTS.md).

| Claim | Current evidence | Permitted conclusion |
| --- | --- | --- |
| Receiver rejected this ingress before file-transfer dispatch | Captured Coordinator-issued guard publication, explicit receive/mismatch/ignore chain, producer regressions | Rejection observed; no inferred file-transfer delivery |
| This is the earlier authorized physical payload under a validated older security/publication context | Outer MessageId fingerprint and route fields only; required identity/context/use links absent | Unproven; do not label it proven stale |
| The existing second-Tuna delivery-recovery condition passed | No exact Tuna resend/receive for the first range | Unresolved; retain current counts and failure |

The second Coordinator-published Tuna-v4 epoch, matching SHA, complete capture,
clean source provenance and Snapshot parity all passed. None supplies the
missing identity or delivery links.

## Executable finding: authenticated metadata is not an incarnation

`NknTransport_KeyReinstallationCanRepeatAuthenticatedMetadataForDifferentPayloads`
establishes a real approved fake-network session, then deliberately calls the
actual private `SetControlSessionSharedKey` boundary. It calls the production
`CreateSecureFileTransferPayload` before and after another key installation.
Both payloads decrypt with their respective derived file-transfer keys.

Two controls cover reinstalling the same control key and installing a different
control key. In both, secure sequence restarts at 1, and the complete decoded
metadata tuple is equal while plaintext and encrypted bytes differ. With the
different key, decrypting the second payload using the first key fails.

The proposed `ftauth1 = SHA256(domain || canonical AAD)` would therefore repeat
for these two different payloads. `BuildAuthenticatedData` encodes only the
metadata tuple. Equal AAD does not establish a security installation or payload
identity. The outer MessageId is also absent from authenticated codec data.

This is a test-only evidence counterexample, not a demonstration that ordinary
reconnects reuse a session, replay protection fails, or unauthorized delivery
occurs. The test deliberately exercises the installation boundary, does not
dispatch a file-transfer frame, and creates no safety classifier.

## Revised capture requirements for the next evidence-only slice

The following is a design candidate, not an implemented or validated schema.
Do not assign production event/token names or enable positive results yet.

1. **Local installation boundary.** Record an opaque local installation identity
   and monotonic generation when a validated session key is installed. Retire it
   on every replacement/reset, including same-key reinstall. Bind it to the
   actual transport instance and verified writer/run, not just SessionId,
   Coordinator instance, process ID or secure sequence. Legacy/private test
   installation without validated establishment evidence remains unbound.
2. **Peer-comparable establishment evidence.** The current approval path already
   has the exact encrypted approval bytes on the sender and authenticated
   receiver. A domain-separated digest of those bytes is a candidate shared
   reference that needs no wire change. Publish it only after the existing
   approval/session/peer checks and actual installation. Never hash/log keys,
   raw peer identities, plaintext or approval grants. A repeated reference under
   another local installation cannot silently reuse the old lineage. This
   candidate still needs a production-shaped proof; a digest alone proves no
   installation or authorization.
3. **Exact payload binding.** Capture the exact encrypted file-transfer bytes
   used for the send and the exact bytes successfully authenticated on receive,
   with separate versioned diagnostic-domain hashing. Bind them to the captured
   installation and codec metadata; keep the existing outer fingerprint as an
   additional check, not as authenticated payload identity. Fresh ciphertext
   with an old outer ID cannot borrow old source evidence. This reference proves
   byte correspondence only within validated producer/context boundaries.
4. **Capture, do not re-read.** Take the key/context identity and allocated secure
   sequence from the same operation. Carry immutable evidence across async send
   completion; do not sample the latest key generation, next-sequence counter,
   route cache or publication afterward. Reset/replacement during encryption,
   authentication or callback handling makes that diagnostic association
   unproven; it must not grant runtime authority or change the existing outcome.
5. **No positive classification.** Missing or inconsistent evidence is
   unproven/not-evaluated. This first slice must leave every recovery field,
   raw/distinct/recovered/unresolved count, hard failure, route policy and
   primary GUI/operator verdict unchanged.

The exact hash domains/byte encodings, installation-retirement events, lock/
async capture boundary and privacy redaction allowlist must be pinned by tests
before production capture is added. Do not copy an untested cryptographic design
from this review into runtime code.

## Remaining publication, ownership and handoff requirements

The current `FileTransferDataPathSendAuthority` carries a copied route tuple,
not the Coordinator instance/boundary/operation/receipt identity demanded by
the parked contract. The private evidence design must bind the send's captured
authority use to one exact validated publication, and the receive guard to its
own captured publication. A later matching route-selected record is not proof
of send admission. Avoid a public DTO extension; if a private producer-supported
join is unavailable, return to design review instead of inventing one.

The existing writer ledger validates writer-start run identities and sequence
continuity. Consume its validated per-writer bindings plus the exact capture
receipt. A global PASS flag or fixture-supplied writer label is insufficient.
Do not infer endpoint role from the numeric process prefix in a writer ID.

Current cross-direction route consistency compares ordered epoch/route/protocol
sequences. It is not the required cross-writer causal publication-use link.
The paired-proof fixture demonstrates useful frame/receipt identity matching,
but still is not a production two-writer validator. A future link must name
both exact publication references and a validated matching local/peer proof,
with duplicate/ambiguous/wrong-writer/restarted-source cases rejected.

Export only a bounded, detached projection from already completed authoritative
full and independently regenerated compact evaluations. Validate equality of
the exact logical links, not only counts or an existing aggregate boundary
hash. Preserve original source locations separately. No second Snapshot
evaluation, reconstructed authority, optional-diagnostic budget extension or
primary-result dependency is permitted.

The GUI currently persists the completed primary summary before its optional
companion; the worker's eligibility carrier contains no publication projection.
Any new projection must preserve that ordering and the existing failure result
under missing/tampered/over-cap input, timeout and serialization failure. There
must also be an explicit retained-success/GUI-success path if later policy wants
these diagnostics there; current error-companion behavior is not such a path.

## Offline verification order and stop boundary

1. Pin the evidence-only security-context capture contract. Test same/different
   key reinstall, reset during async work, metadata reuse, reused outer ID with
   fresh authenticated bytes, wrong session/peer, malformed authentication and
   budget-estimate encryption (which must not create actual send evidence).
2. Prove generated source/receive evidence through real producer/redactor paths
   with immutable captured context and no altered encryption/replay/dispatch.
   Use synthetic keys only; do not dump live credentials or payloads.
3. Validate writer ownership and exact publication/paired-proof links with
   production-generated histories; prove full/regenerated-compact projection
   parity and primary preservation. Keep classifier output disabled until all
   prerequisites pass.
4. Only then consider a diagnostic classifier. Rejection safety must not be
   counted as delivery recovery. Any acceptance cutover needs its own explicit
   rule and independent delivery obligations, not a blanket suppression of
   `file_tuna_data_path_mismatch`.

Another live 256 MiB attempt is not useful for filling missing fields in the
existing capture. It becomes necessary only after an approved runtime evidence
producer is implemented and the directly affected offline gates pass. Rebuild
the portable and run strict escalated preflight before that attempt. This review
does not start it or retroactively alter earlier results.

## Source anchors and verification

- Codec metadata/AAD: `SessionSecureEnvelopeCodec.cs:243`.
- Send allocation: `NknSignalingTransport.FileTransferTransportChannel.cs:5620`.
- Key install/reset: the same file, lines 6191 and 6229.
- Validated approval paths: `NknSignalingTransport.LifecycleChannel.cs:1283`
  and `:2139`; both existing installation call sites were inspected.
- Captured route DTO: `IFileTransferSignalingTransport.cs:663`.
- Writer/run evidence: `GuiSmokeLogLedger.ps1:329`.
- Cross-direction sequence comparison: `CoordinatorSnapshotEvidence.ps1:15823`.
- Primary persistence before companion: `GuiSmoke-Windows.ps1:38027`.

The new two-case counterexample passed. No production, public DTO, wire,
Snapshot catalog, acceptance or historical artifact changed. See
`artifacts/security-provenance-contract-review-20260913/RESULTS.md` for final
focused verification and the exact working-file set.
