# Canonical committed-prefix barrier: bounded implementation

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: **test-only implementation and focused deterministic verification complete;
native full-size validation remains outstanding**.

## Implemented contract

The fourth retained source reader now waits before its first nonempty tail read
at byte 268,283,904. Pre-arm remains at 268,219,392 and does not wait. Empty reads,
seeks, ordinary prefix reads and the existing zero-byte EOF behavior do not consume
the gate. Initial late entry, crossing the boundary, competing pending entries,
missing binding, cancellation, source retirement or changed source position fail.
Partial reads after valid entry reuse the consumed gate. Source currentness is
checked after the await before consumption and again before the original read.

The original receiver checks the exact committed prefix under its service gate,
including retained context, data-session reference/generation, fallback runtime
owner/publication, local lease, retained peer and original carrier. This uses the
existing pure coordinator currentness predicates. Geometry, manifest, pause and
terminal state, committed counters/bytes, highest receipt, pending writes,
buffered bytes, bitmap length and absence of a prior bound frontier episode are
required. Cleared committed-prefix bitmap bits are valid. No progress is written
and no authority evaluation, route publication or repair is added.

One dedicated current-user-only, single-instance pipe is established before
payload release and authenticated with `AttemptPeerProcessChannel.MatchesPipe`.
The sender reader is already asynchronous. The versioned scalar record binds both
app requests, session/transfer, fixed geometry and exact committed boundary. The
body is limited to 512 bytes; length/body/EOF are required, with at most one extra
byte read to reject trailing input. The producer writes once and closes, without
an acknowledgment, reconnect or retry. Original carrier/context cancellation is
retained; cleanup cancels rather than successfully releasing a waiter.

Receiver prefix polling runs before suffix polling. The descriptor observer and
consumer reject cached ordinary tail frames unless the original reader entered
the exact boundary and consumed the handoff. Metadata failures retain the existing
latched failure path before physical output. No cache flush or batching workaround
was added.

## Verification

67 distinct new focused cases passed across incremental runs. These cover read
entry, partial/empty/seek behavior, cache bypass, cancellation/disposal, exact and
inconsistent prefix shape, original context/data-session/runtime/publication
retirement, request mismatch, framing/size/truncation/trailing/duplicate input,
EOF ordering and post-EOF currentness. All three pre-arm alignments complete a
real asynchronous pipe delivery, file write and reverse pipe feedback while the
tail source read remains blocked. The unit fixtures are not full-size/native
transfer evidence.

The directly affected regression lane passed **176/176** once after stabilization:
canonical profile and carrier predicates, descriptor/hash provenance, unchanged
scheduling/release behavior, original hold-budget limits, one-shot timing
diagnostics and pipe write completion/failure. Total distinct passing checks:
**243**. After the last test-only Windows guards, only the 15 affected read-gate
cases were rerun; no unrelated aggregate suite was repeated.

Development failures are retained in the artifacts: two test compile mistakes
(metadata argument order and enum spelling) were corrected; the first real-I/O
test serialized reverse write completion ahead of its reader and timed out in all
three cases. Starting the reverse read concurrently, as the carrier already does,
fixed that test-only sequencing error. The corrected three cases and final 15-case
read-gate run pass. No runtime timer or production code was changed to make tests
pass.

The scheduler class body and native read factory/four-reader probe are byte-for-
byte unchanged from the entry copies. Core and Infra.Nkn DLL hashes, including
their test-directory copies, remain unchanged. Only the test DLL was rebuilt.
The production wire carrier, exact four-second hold start/expiry, suffix repair,
recent recheck, publication window, same-pair acceptance and release order remain
unchanged.

No native process-pair capture, GUI run, live NKN check, portable package rebuild,
commit or push was performed. The real retained-process endpoint check is reused
and source-reviewed here; its cross-process integration and full-size timing are
still native-validation gates, not claimed by the in-process pipe tests.

## Preservation and next step

Entry matched all 4,482 prior pins and all 564 dirty entries. Before-images,
incremental TRX results, changed-file hashes and the final preservation audit are
in `artifacts/canonical-prefix-barrier-implementation-20260929/`. Existing work is
preserved; the changes in this step are six existing test files, four new test
files and this report. No production source was edited.

Next: review the pinned implementation/build, rebuild the required native/portable
fixture and perform **one separately gated canonical native capture**, with its
existing preflights and original deadline. Require the prefix witness before the
first physical tail, no cached-tail bypass, unchanged four-second holds and
advancement of the original repair edge. Do not run the full GUI matrix or weaken
acceptance if a later proof edge fails.
