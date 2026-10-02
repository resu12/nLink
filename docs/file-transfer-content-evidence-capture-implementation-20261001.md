# Sender content evidence transport integration

2026-10-01. Base checkpoint `7ddbe15c3b894cf2497207cc39a08189b7578cc0`.
The capture-only producer from the
[reviewed contract](file-transfer-sender-content-evidence-contract-review-20261001.md)
is now integrated and verified offline. It exports sender-local chunk commitments
for later rebatching analysis. It does not change route authority, wire data,
transfer progress, retry, classification or acceptance.

## Implemented behavior

Actual V4 batch sends carry their originating data-session object privately into
the raw send path. Capture requires its canonical scope, the security installation
used for encryption, an explicit outbound V4 authority and that exact authority
object's existing physical-submission publication association. Missing, copied,
wrong-scope, pre-pump or incompatible route evidence produces no content fields.
V6, control frames and budget estimates are excluded. Trace-disabled operations
allocate no diagnostic key or content projection.

One active diagnostic slot binds the sender owner and security installation.
Regular and Tuna publications for that owner retain the same random secret.
Competing owners cannot replace the occupied slot. Security reset or reinstall,
accepted termination, exact owner removal/disposal and transport disposal detach
the slot and zero its owned secret. Same-key reinstall creates a fresh scope;
old captured contexts cannot borrow it. Delayed terminal sends carry their
original diagnostic owner, and late old removal cannot retire a replacement
owner's scope. These references never grant runtime authority.

Capture hashes exact serialized segment slices and freezes their index, length
and commitment before transport awaits. The emitted evidence retains no payload,
secret or adapter owner. Only the canonical successful send-result row receives
the bounded suffix, after its scope, range, count and raw-byte total agree with
the captured tuple. Failed/canceled sends emit no successful content record;
retired evidence carries no usable segment commitments.

Redaction protects only the exact canonical profile, fields, slot order and token
shapes on a single send-result event. Malformed, duplicate, ambiguous, unrelated
and lookalike fields receive no exception. No receiver commitment, diagnostic
key sharing, new log event, queue, payload cache or background worker was added.

## Verification

The final focused lane passed 43 lifecycle, binding and redaction cases using a
workspace-owned private log. It covers opt-out, estimates/non-V4 input, absent or
copied publication associations, caller-byte mutation after serialization,
conflicting row metadata, failed/canceled sends, retirement and key zeroing,
same-key reinstall, stale contexts, competing owners and delayed old callbacks.

The affected security, publication, redaction and lifecycle lane exercised 68
existing cases. Eight initially failed only at persisted-log reads against the
default AppData location. Those same eight passed with the existing private-log
configuration inside the writable workspace, without source or limit changes.
The failed receipt remains preserved. This supports an environmental persistence
explanation, not an application authority or dispatch RCA.

Seven analyzer fixture cases passed on both PowerShell 5.1 and 7: two rebatching
cases, three existing exact-payload controls and two independent-pair cases.
For rebatching, the original encrypted identity binds the held receive to one
sender row. That sender's commitment equality matches the fixture's private
decoded-wire comparison: true for identical bytes and false after a byte change.
The exact Tuna send/receive binding, three original evaluations, source custody
and full/compact parity remain enforced. Both rebatching cases still fail the
unchanged strict rule with `missing_tuna_resend`.

The final lookalike-field redaction tightening was followed by the 43-case focused
rerun; unrelated analyzer lanes were not repeated. The
[verification receipt](../artifacts/content-evidence-capture-20261001/verification.json)
records 118 distinct passing final test outcomes, source/assembly pins and the
preserved initial failures. It also confirms unchanged primitive source/tests
and all 99 original live-artifact hashes. `git diff --check` passed. Existing
compiler/analyzer warnings remain.

## Remaining boundary and current state

This producer supplies evidence, not a rebatching acceptance decision. The
historical live run has no new commitments and its verdict is unchanged.
No consumer, classifier or acceptance cutover was implemented, and no log,
witness, throughput or work limit was raised.

Recommended next step: review the bounded content consumer against the three
required bindings and primary-owned witnesses before implementing it or adopting
a different acceptance rule. Missing, changed, retired, ambiguous or late
evidence must remain unproven. A later targeted portable/live run can validate
newly exported evidence; the full GUI matrix is not the next gate.

All accumulated work remains uncommitted on `v0.7.0`. This step performed no
portable rebuild, live NKN/GUI run, commit or push.
