# C1m: bounded private publication completion

Date: 2026-09-14. Base HEAD `ef78e9d7`, with accumulated uncommitted work.
This adds primary-local immutable completion and payload preparation. It does not
add a completion channel, parent adoption, or live export.

## Issuer ownership

C1l's private custody scope now binds the genuine C1c native capture and immutable
token before either original evaluation is recorded. The typed token must belong
to that capture and retained request; its four role counts must match the actual
full/reduced arrays. Another capture/token with matching hashes and counts is not
the same owner. Cancellation of the bound native capture invalidates custody.

The existing shared ownership loader compiles the source, GUI, read-lease, native
capture and completion classes together. This permits genuine typed ownership
checks instead of reflection, caller-authored descriptors or duck-typed objects.
The native loader reuses that loader. Incompatible previously loaded types cannot
enable completion; fresh supported workers load one consistent assembly.

At the original first-failure seam, C1k retains its independently derived canonical
full/reduced semantic rows, exact witness and full/reduced/physical provenance.
Its existing diagnostic `Json` output remains unchanged. C1m never reads that
field, accepts a candidate payload, or accepts a caller-supplied expected digest.

Only the actual primary passes its privately held custody/pair to the trusted
issuer adapter. Both are genuine types, not deserialized copies. The issuer checks
the exact context, arrays, returned views, native owner/token, and the worker's
exact still-verified writer reader. The full/reduced semantic components must
agree. The adapter keeps its capability and primary-reference parameters private
before invoking the canonical metadata codec; ordinary `private:` assignment to
an existing PowerShell parameter is insufficient, so variable options are set
explicitly. No issuer is passed to the publication resolver or optional formatter.

This is the trusted primary producer boundary, not an authentication service for
arbitrary callers already holding its capability. Canonical components originate
from the primary's existing independent A/B1 checks; their printable-ASCII/size
admission in C# is not a general JSON parser or a second semantic evaluator.
Two invented equal rows do not independently establish Coordinator authority.
Same-account malicious code or explicit process/scope introspection is outside
this local-worker isolation premise.

## Immutable payload and completion

The private builder constructs a distinct `nlink-local-publication-forwarding-v2`
payload containing the actual request metadata, separate source/writer file-byte
descriptors, four role-specific native descriptors, catalog identity, both original
invocation IDs, and exact witness/semantic-row/provenance selection. Native inputs
use `nlink-ordered-utf16le-v1` and have no invented file paths. Original file hashes
and file-object identities are retained independently.

The request projection is explicitly named
`nlink-local-publication-prepared-request-v1`. It is not the completed parent-owned
producer request/channel contract: the future output namespace and transport
binding do not exist yet. Do not reinterpret the producer-request-v1 wire format
or C0's five-file V1 carrier to accommodate this intermediate prepared projection.

The builder computes request, input-descriptor, selected-row and payload SHA-256
values itself, and binds the payload byte length, retained request ID, actual worker
PID/creation time and both invocation IDs in an immutable `PublicationCompletion`.
Selection hashing covers the row, witness and original/native provenance together.
It adds no accepted/recovered/eligible verdict, runtime authorization or peer proof;
scope remains local and peer lineage is `not_evaluated`.

`PreparedPublication` and `PublicationCompletion` have no public constructors or
setters. Payload access returns a bounded copy, never the owned byte buffer. An
exact object-reference ownership check rejects serialized lookalikes even with
matching hashes. Issuance is one-shot. The low-level replay returns no second
completion and does not substitute the first. Adapter failure disposes its private
scope, with whole omission and no retry.

Native/source/writer/worker lifetime remains required for owned completion. Scope
disposal invalidates the private payload and drops retained references. Worker
disposal also disposes the scope. The primary keeps completion private and drops
it in `finally`, including report/evaluator failure; ordinary report and external
failure-witness behavior is unchanged. A detached metadata copy alone is not an
adoptable completion.

## Preparation cost and reporting boundary

All added work runs before primary report publication. It does not scan, hash,
copy or re-evaluate original input arrays or private views. Existing C1k row
canonicalization is retained and reused; new witness/provenance serialization is
over fixed detached scalar structures, not an unrestricted private-view graph.

The builder admits at most 262,144 aggregate characters across the five canonical
components, including both independent semantic rows. Each component and final
payload is capped at 262,144 ASCII bytes. Its private fixed-schema object writer
admits at most 16 fields per object and rejects before an append exceeds that
object's cap. Accounted character and byte visits are each capped at 2,097,152.
These counters cover admitted/appended characters and encoded/hashed bytes, not
every interpreter instruction, string comparison, allocation or UTF-8 validation
operation. Temporary structures are bounded metadata/component strings and the
final byte array, never whole original views or input buffers.

The adapter resumes the existing 2,000-ms optional-work clock. The C# builder gets
only the remaining time, checks owner/deadline at phases and bounded character
intervals, and is followed by the original-clock check. No deadline, grace period
or watchdog is extended. These are cooperative bounds, not scheduling guarantees
or live 256 MiB capacity claims. After reports, only existing bounded cleanup is
performed: no new serialization startup, whole-input work, I/O, IPC or wait.

## Still required before live adoption

Follow-up: [C1n](file-transfer-primary-publication-channel-contract.md) implements
the optional full request/namespace binding and private completion transport.
The non-channel C1m prepared-request representation remains unchanged. Upstream
relay and real post-terminal-receipt adoption are still disabled.

The separately parent-owned completion channel, full output-namespace/request
binding, dynamic expectation adoption, upstream GUI-owner relay and unchanged
post-terminal-receipt lifecycle gates remain disconnected. The immutable local
completion is necessary preparation, not permission to accept candidate bytes at
the parent. C1a's pre-known-digest slot is unchanged; no arbitrary digest setter,
stdout completion, primary receipt rewrite or live caller is added.

Verification: `artifacts/publication-primary-completion-20260914/RESULTS.md`.

The completed gate passed 13 unique xUnit tests, including 54 actual generated
worker cases across PS5.1/7 and the directly affected native, custody, report and
parent/writer regressions. Four additional focused worker checks cover Unicode
path metadata and final payload-size rejection after component admission. Positive
generated cases used 508–697 ms on the shared optional-work clock and produced
4,964–4,973-byte payloads. These measurements do not establish live 256 MiB capacity.
