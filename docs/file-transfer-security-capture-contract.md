# Evidence-only security-context capture contract

Date: 2026-09-13. Scope approved after the bounded provenance review: private
transport evidence capture only. This does not enable a classifier or change
cryptography, key derivation, replay, dispatch, authority or acceptance.

Implementation: private capture and focused producer/privacy regressions added.
Final offline results are recorded in
`artifacts/security-capture-implementation-20260913/RESULTS.md`.

## Producer contract

- Every transport has a random, opaque `ftst1-` + 32 lowercase hexadecimal
  identity. Every actual key installation has an independent random `ftsi1-`
  identity and an increasing local installation generation.
- The two existing validated approval paths supply their exact encrypted
  approval bytes and validated SessionId at the existing installation call.
  `ftest1-` is lowercase SHA-256 over UTF-8
  `nLink/filetransfer/security-establishment/v1` followed by one zero byte,
  followed by those exact bytes (no JSON/base64 reserialization).
- A legacy/private installation has no establishment/session binding. It must
  never inherit the previous installation's reference. Every replacement,
  including same-key reinstall, retires the old context. Reset retires it too.
  Retirement is diagnostic state only, never a runtime cancellation token.
- Key copying and context capture occur under the existing key-state gate.
  Existing sequence allocation and codec operations remain in their original
  order. Each operation carries its actual allocated/authenticated sequence;
  it never reads the latest counter to reconstruct evidence.
- `ftpay1-` is lowercase SHA-256 over UTF-8
  `nLink/filetransfer/security-payload/v1` followed by one zero byte, followed
  by the exact encrypted file-transfer payload bytes. It is distinct from the
  outer MessageId fingerprint. It does not hash plaintext or keys.
- Only actual data-frame send preparation captures payload evidence. Budget
  estimates and other message families do not. Send-result diagnostics retain
  the captured context across the existing asynchronous send, and are emitted
  only where the existing successful send-result diagnostic is emitted.
- Receive capture is returned only after successful authenticated decode and
  the existing replay check. Data-path diagnostics are still emitted only
  after the existing frame/session/peer/transfer validation. Rejected
  authentication, replay or metadata/session validation creates no positive
  receive reference. No diagnostic makes a rejected frame dispatchable.
- The immutable payload reference points to its original installation, never
  a later installation. A retired context is reported as `retired`; an absent
  approval binding or mismatched session is `unbound`. `captured` means local
  evidence was captured, **not** validated cross-writer provenance or safety.
  A consumer must also reject evidence after a logged retirement; a prior
  `captured` string cannot override a later lifecycle event.

## Diagnostic surface and privacy

Installation/retirement records are `filetransfer_security_context_installed`
and `filetransfer_security_context_retired`, written through the existing
operational logger. Its persisted writer ID, sequence and run-start ledger are
the ownership source; no environment-derived duplicate writer authority is
added. A future consumer must validate that ledger and exact capture receipt.

Existing selected data-path send/receive/peer-visible/mismatch/ignore events
carry these additional evidence fields:

- `security_transport_id`, `security_installation_id`,
  `security_installation_generation`, `security_establishment_fingerprint`;
- `security_context_status`, `security_payload_fingerprint`,
  `security_sequence`, `security_session_id`, `security_transfer_id`.

Session/transfer values use the existing `fte1-` scope projection. No raw
identity, key, approval grant, encrypted bytes or plaintext is logged. The
redactor protects only exact versioned token shapes on the seven selected
events, with one event per line; wrong prefixes, extra suffixes, unrelated or
ambiguous events and arbitrary neighboring fields are not protected.

No public DTO, wire schema, Snapshot catalog, analyzer selector, recovery field,
count or primary verdict is changed. References remain best-effort evidence.
Missing log records or ambiguous repeated establishment references are unproven.

The new installation/retirement event names are not added to the existing
semantic retained-event catalog in this slice. They are present in the raw
operational log, not guaranteed in a compact/semantic retained slice. A future
consumer must pin the complete source including installation and retirement
history; the data-path suffix or a global writer PASS alone cannot replace it.

## Offline gates and remaining boundary

Tests must cover same/different-key reinstallation, reset/replacement while a
send is awaiting completion, equal metadata/different payloads, reused outer
IDs, malformed authentication, wrong session/peer, replay rejection, no budget
estimate evidence, exact send/receive byte-reference equality and privacy
through the real producer/redactor path. Existing focused receive-guard and
security tests must retain their outcomes.

This slice supplies no Coordinator publication-use reference and no validated
cross-writer causal link. Those remain separate prerequisites from the review.
No positive stale classifier or acceptance cutover is authorized. Stop before
a new live 256 MiB test; do not rewrite historical artifacts with new evidence.

## Next live evidence-validation gate (not run)

The offline producer uses two transport instances in one fake-network process.
A fresh targeted `regular-v4-live-activation-off-on-off-256mb` run is needed to
validate these references across real processes. Before that run, checkpoint
the reviewed source, rebuild the portable and pass the strict escalated bridge
preflight. Do not bypass a preflight failure.

Retain and hash the complete raw operational log as well as the normal finalized
slice/receipt. Verify each peer's writer-start/run/continuity and exact local
installation history, then compare the approval and encrypted-payload references
on actual send/receive records. Require matching authenticated sequences and
session/transfer scope; do not pair installations by generation or timestamps.
Retired/unbound/missing/ambiguous evidence remains unproven.

This is validation of the new producer, not a prediction that acceptance will
pass. The strict second coordinator-published Tuna-v4 route and existing exact
Tuna delivery obligations remain intact. No classifier, publication-use join
or retrospective recovery is supplied by this slice, even if its live evidence
checks pass.
