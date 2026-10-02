# Content evidence primitive implementation and verification

2026-10-01. Base checkpoint `7ddbe15c3b894cf2497207cc39a08189b7578cc0`.
This implements the first slice of the
[sender content evidence contract](file-transfer-sender-content-evidence-contract-review-20261001.md).

## Implemented behavior

The internal Core codec projection returns canonical session/transfer IDs and
at most three chunk indices, offsets and lengths. It reads the existing
serialized V4 bytes without copying segment payload or retaining a span. It
shares the codec reader, frame-code constant, normalization and range checks.
The existing allocating decoder remains unchanged. Empty, malformed, trailing,
out-of-bound, V6 and control-frame inputs provide no projection.

The internal Infra helper owns a fresh 32-byte diagnostic key and opaque scope
ID for its supplied canonical session/transfer. It streams the reviewed
length-prefixed HMAC input and exact projected segment slices into the hash
provider. Identical chunk content produces the same commitment when rebatching
from one to three segments within that scope. Changed bytes, logical index or
length change the commitment; another session/transfer is rejected, and a fresh
scope produces a different commitment.

Capture returns frozen commitments and a nonsecret retirement marker, with no
payload or owned key reference. Disposal synchronizes with capture, marks the
scope retired and zeroes its owned key. A retired scope cannot capture again.
The internal formatter suppresses retired commitments and enforces the reviewed
768-byte suffix bound. Its output is ASCII and invariantly formatted; canonical
IDs and keys are not included in that suffix.

## Verification

The stabilized focused run passed all 31 primitive cases. Coverage includes
one/two/three-segment correspondence with the unchanged decoder, Unicode and
normalized IDs, every truncated prefix of a fixture, malformed shape and scope,
maximum serialized size and final valid chunk range. Allocation checks confirm
that projection allocation does not scale with segment payload size.

The HMAC cases include an independent preimage calculation, rebatching equality,
same-length changed content, index/length changes, another scope, immutable
capture after source mutation, malformed input without partial output, owned-key
zeroing, idempotent retirement and invariant bounded formatting.

Ten affected existing codec/provenance regression cases also passed: explicit
legacy V4 codec round-trip, rejection of legacy binary frame codes through the
default codec, and both queued Regular rejection provenance cases. The initial
30-case run passed before the final small follow-up edits; the stabilized
31-case receipt is authoritative for the final sources. No aggregate suite or
new PowerShell-host analyzer run was needed for these unwired primitives.

The [verification receipt](../artifacts/content-evidence-primitives-20261001/verification.json)
pins final sources, tested assemblies and both final test runs. It confirms that
the four earlier rebatching test edits are unchanged and rechecks all 99 original
live-artifact pins. `git diff --check` passed.

## Remaining integration boundary

These helpers have no production callers. They do not yet bind an originating
data-session owner or security installation, observe transport terminal/reset
hooks, capture publication associations, or export redacted operational records.
Scope retirement tests verify the helper's explicit disposal, not the future
transport lifecycle integration.

Next, connect the capture-only producer to actual V4 physical sends under the
reviewed owner/installation/publication bindings. Add exact lifecycle and
redaction tests before enabling log export, then reuse the two rebatching
fixtures on PowerShell 5.1 and 7 while preserving strict `missing_tuna_resend`.
No correlator or acceptance change belongs to that slice.

Transport behavior, wire format, Coordinator authority and historical verdicts
remain unchanged. No portable rebuild, live run, commit or push was performed.
