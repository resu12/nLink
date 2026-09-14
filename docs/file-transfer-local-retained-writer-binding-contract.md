# C1i: accepted GUI writer context bound to retained source ownership

Date: 2026-09-14. Base HEAD `ef78e9d7`, with accumulated uncommitted evidence
work. This closes the typed writer/source association at the retained-parent
boundary. It does not implement the Wrapper-to-RouteAcceptance process relay,
activate the primary's optional writer traversal, or issue V2 completion.

## First missing edge

[C1h](file-transfer-local-gui-writer-request-contract.md) produces a real immutable
GUI completion, but [C1f](file-transfer-local-retained-source-request-contract.md)
previously carried only source ownership. Loading ledger JSON or copying matching
hash fields would not convey the accepted GUI owner. A writer's historical file
identity also cannot prove its current bytes after the native write handle closes.

The bounded fix has two parts: convey the accepted typed context only when it
matches the existing source hash owner, and expose a separately verified writer
reader under that same source/worker lifetime. No extra pre-launch writer hash is
performed, and primary inputs are not changed to make them eligible.

## Parent association and wire format

Both retained-parent functions accept an optional `LocalWriterContext` argument
alongside the existing default-off `CaptureLocalSourceOwnership` switch. Neither
CLI nor any live call site enables it. The source helper attempts
`LocalRetainedSourceRequest.TryCreateWithWriter` using an actual
`LocalGuiWriterRequest.Completion` and the actual `LocalInputReadLease` from the
parent's original hash traversal. It requires exact acceptance/scenario IDs,
canonical analyzer pathname, file-byte SHA-256, byte count and original decoded
line count. GUI and retained attempt GUIDs remain distinct fields; the latter is
still the original primary attempt GUID, not a new attempt.

Null, duck-typed, JSON-round-tripped, cross-scope, copied-path, changed-source or
milestone-merged contexts cannot establish writer ownership. The helper falls
back to the original **source-only** request, preserving that existing capability
and the primary parse. A rejected association does not suppress normal analysis,
strip appended evidence, change reports, or authorize a second evaluation.

C1h now retains its already bounded original completion body privately (at most
64 KiB), in addition to immutable decoded facts. Only the assembly-internal codec
can copy it into the new request. There is no public serializer/deserializer or
completion constructor, and the formatter does not receive an issuer. The private
body is never a candidate payload's self-declared expectation.

The original source-only magic/body and 16 KiB ceiling remain unchanged. A distinct
writer-bearing magic adds the GUI request ID, original GUI PID/creation stamp,
artifact root and original bounded completion body to the existing source fields.
The combined request body is capped at 96 KiB. Decoding uses an explicitly bounded
header view and a separately bounded body view; it does not infer logical size
from Windows' page-rounded mapping capacity. Unknown, truncated or over-cap
records omit ownership rather than parse a partial association.

Attachment retains C1f's exact retained-worker PID/creation/request and live
parent-handle checks, with zero-wait locking and one attachment attempt. The
read-only worker decoder independently checks the writer/analyzer association
against the request's source descriptor. The historical GUI identity is preserved;
it is not mistaken for the current retained-worker identity or a live process.

`LocalInputReadLease`, `LocalRetainedSourceRequest` and `LocalGuiWriterRequest`
compile together, making the prerequisite a real CLR type instead of a PowerShell
shape check. Both loader orders use the same assembly. Incompatible previously
loaded types can omit the optional capability; they must not trigger a second
primary hash or replace normal source parsing.

## Writer candidate and verification lifetime

An attached worker exposes its immutable `WriterContext` as historical metadata.
That property does **not** mean writer bytes have been verified. `TryOpenWriter`
is one-shot and requires the same worker's source reader to have reached exact
EOF successfully. A missing context, early call, partial/non-EOF source read,
disposed source or inactive parent returns no writer reader.

The candidate is opened read-only through C1e `OpenExpected`; its actual volume/
file identity and size must match the original native writer descriptor. Missing,
replaced or size-changed candidates are omitted before traversal. Matching identity
and length only pin a candidate: a same-file/same-size byte change is rejected by
the separate SHA check, not accepted on identity alone.

`WriterReader.VerifyAndReadLines` delegates the admitted traversal to C1e's
existing strict decoder, incremental byte hash and EOF/count checks. It adds no
second writer traversal. C1e's fixed ceilings remain: 64 MiB bytes, 262,144 records,
67,108,864 code units, 262,144 code units per line and 250 ms cooperative elapsed
verification time. Partial reads, invalid bytes/counts, budget exhaustion or
cancellation omit verification. Public `IsVerified` also requires the live
parent/worker and the same completed, undisposed source reader.

The worker owns the candidate lease and disposes it even if a caller retains the
writer-reader wrapper. Source disposal or parent cancellation invalidates the
wrapper's verification status. No raw mutable lease or completion issuer is
returned. Existing primary source-reader errors and framing are untouched.

No production caller invokes `TryOpenWriter` yet. The future prepared-context
owner must perform this bounded traversal before report publication and bind
exact source/writer/native membership and the existing evaluator contexts. It
cannot call this API after reports as a new whole-input scan.

## Primary compatibility and remaining work

Actual retained-parent launch, 1,800-second watchdogs, exit/proof checks, report
bytes and immutable terminal receipt ordering are unchanged. No primary
orchestration/evaluator code changes in this slice. Verification success is local
input evidence only, not route, runtime, coordinator or publication authority.
It may exist before an analysis later fails or times out; it does not convert
that outcome into success or an adopted V2 export.

The upstream Wrapper-to-RouteAcceptance relay remains disconnected. The route
launcher currently invokes its wrapper through the existing child-script path;
it does not yet own/adopt a typed GUI completion across that process boundary.
These tests pass the actual accepted GUI context to each extracted retained-parent
function in the same test host; they do not claim that upstream production relay.

Next, bind the actual primary's bounded writer/native membership preparation to
its existing full/reduced evaluator contexts and first-failure witness, then
complete the process relay and separate V2 completion/adoption gates. Do not
reconstruct private views from saved summaries or enable live export yet.

Results: `artifacts/publication-retained-writer-20260914/RESULTS.md`.

Follow-up: [C1j](file-transfer-primary-evaluation-input-contract.md) integrates
the bounded input/evaluator association into the compact primary. Actual writer
verification, selected membership and A/B1 preparation remain pending before any
completion issuer can be enabled.
