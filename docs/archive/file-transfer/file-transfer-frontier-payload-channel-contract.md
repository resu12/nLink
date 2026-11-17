# Frontier boundary payload and native channel contract

2026-09-15. HEAD `2691fc70f98d668bae943ebd41c6f8540156ec57` plus A1-A7c.
This review separates producer preparation, native writer/source custody, and
primary-to-parent diagnostics. It does not approve a correctness disposition.

## Source findings

1. A7c's `FrontierBoundaryObservation.TryFreeze` requires completed input-log
   anchors and embeds that input or its selected raw lines and input hash. That
   diagnostic export cannot also be the producer payload whose marker belongs
   to the same completed log: doing so creates an ordering/self-reference cycle.
   The producer payload must be independent of later log/source selection.
2. A7c's private projections contain raw source/token material and raw log lines.
   Their internal validation does not approve persistence of that representation.
   A new payload needs an explicit export projection, not whole-object dumping or
   a generic redactor applied to the old JSON.
3. `LocalOperationalLog` privately owns its writer ID and assigns sequence/batch
   metadata. `Info` is best effort and returns no persistence capability. Its writer
   ID has a process-ID prefix and nonce, but not native process-incarnation proof.
   `WriterIdForTests` must not become a production ownership shortcut.
4. `LocalGuiWriterRequest.TryAttachCore` binds the GUI finalizer's actual worker
   process. That is not automatically the nLink app process whose QPC stamps were
   captured. Its completion decoder owns three native outputs plus the verified
   semantic copy, not an app-producer payload descriptor. App and GUI ownership
   must be connected explicitly; never compare unrelated process roles as equal.
5. The optional `LocalFrontierReceiptJson` analysis path accepts only the existing
   injected profile. The separate `nlink-local-publication-forwarding-v2` channel
   issues a different capability and must not carry a relabeled frontier result.

Relevant sources are the A7c validator, `Logging/LocalOperationalLog.cs`,
`tools/FileTransferOps/LocalGuiWriterRequest.cs`, `GuiSmoke-Windows.ps1`'s native
finalizer and app launcher, and the existing primary publication channel/issuer.
These findings come from current code, not a historical live-run reclassification.

## A8a: closed-producer preparation (implemented)

The new private `TryPreparePayload` takes no log, candidate JSON, caller hash,
source path or request argument. It reuses A7c's closed-positive lifecycle checks
and the unchanged timing predicate. It can run outside the service gate after
accepted completion and before the Coordinator stream closes. It does not read a
clock, perform I/O or invoke authority evaluation.

The format is `nlink-frontier-boundary-producer-v1`, with clock profile
`frontier_publication_boundary_utc_qpc_v1`. Exact canonical UTF-8 bytes use the
declared `sha256-utf8-frontier-boundary-producer-v1` hash domain. The digest is
external to the hashed payload; no input-log hash or self-hash is embedded.
There is no BOM. Encoding is strict, with a 262144-byte maximum; UTF-16 code-unit
length alone is not a byte budget. Lifecycle capacity stays 64 and frame ranges
cannot exceed the existing protocol maximum of 128.

The explicit projection contains:

- Original observation/process-incarnation/clock identity, raw integer boundary
  samples, original D/P, root/recheck/W1 generations, lifecycle order and outcome.
- Source/repair scalar generations and route facts, existing `fte1` projections
  of identifying strings, and hashes of complete private source/token values.
- Actual progress/write quantities, predecessor/successor/recheck links, the
  selected frame's numeric/boolean fields and ranges, projected frame metadata,
  issued delivery identity/generation, and the exact existing accepted Snapshot
  identity/hash. It does not duplicate the accepted Snapshot payload.

There are no raw session/transfer/data-session/checkpoint identifiers, runtime
token objects, file contents, log lines, file paths or private receipt objects in
the exported projection. Evidence hashes identify values; they cannot be decoded
or accepted as runtime tokens. The raw process/clock identities and counter/UTC
values are intentional diagnostics, not proof of native process ownership.

One immutable preparation is cached per closed observation. Returned byte arrays
are copies. Only the observation's exact retained preparation passes its private
reference-ownership check; an equal-byte wrapper, copied DTO or foreign producer
does not. This is trusted local workflow custody, not protection against arbitrary
same-process reflection or a hostile same-account process.

All payloads and preparation objects retain `LiveProofEligible=false`,
`NativeWriterCustodyEstablished=false` and `PublicationCompletionIssued=false`.
There is no runtime activation or persistence caller. The existing A7c JSON,
injected profiles, hash domains and primary behavior are unchanged.

## A8b: native app/writer/source ownership (app-local files and early GUI launch owner; shared-source handoff pending)

The native transport must establish each link below before its output is usable:

| Owner | Required binding |
| --- | --- |
| App request/producer | Original request/run/scenario and actual app process handle/incarnation; exact producer-owned preparation |
| Operational writer | Its own writer identity; digest/byte count/profile/observation marker tied to the exact accepted completion and genuine persisted writer sequence |
| GUI finalizer | Original native GUI request and app launch ownership; completed payload descriptor and exact source/writer completion |
| Native read owner | Immutable file identity/length/hash and retained read lease for the selected payload and source, not path discovery or diagnostic JSON |

Do not manufacture app ownership from the PID in a log ID, a payload field, a
GUI worker PID, an environment variable alone, or a public caller-authored DTO.
The request-selected native endpoint and held process handles must bind both app
and GUI roles across preparation, publication, exit and finalization.

The operational marker must remain digest-only and diagnostic-only. Its sequence
must follow the exact accepted completion's persisted record and precede final
writer drain. Enqueue/send acceptance is not persistence. Missing/reordered/
conflicting marker, failed/partial payload output or incomplete writer drain
omits the optional channel. Do not add I/O, waits or export serialization under
the service gate, and do not let export failure change send settlement.

Before implementing payload transport, specify its separately bounded catalog count,
aggregate UTF-8 bytes, producer admission, preparation/work budget, cancellation
and cleanup. The 64-witness limit is not implicitly a 64-payload catalog limit.
Never evict, choose the newest successful payload, broaden capture, or retry
runtime work to salvage a diagnostic overflow. Private A8a byte tests do not yet
prove native transport or end-to-end export cost/failure containment.

Keep the old native-output descriptor format compatible. A separately versioned
extension must bind payload and source file identities, original request/run/
scenario, app/GUI incarnations, writer ID, observation and accepted completion,
exact byte length/hash and the relevant writer marker. No path scanning, arbitrary
JSON import or use of a caller's asserted digest to establish expected bytes.

### A8b1: isolated native app request/lease (implemented)

`tools/FileTransferOps/LocalFrontierAppRequest.cs` and its separate PowerShell
loader implement the native process-attachment link only. The parent creates a
fresh local named-memory request, retains the exact run/scenario registration,
and binds the actual launched `Process` by a duplicated native handle and native
creation time. It cannot bind itself or an already-exited process. There is one
binding attempt per request; a failed first attempt cancels it rather than allowing
rebinding. The app must match its actual PID/incarnation and hold the still-live
parent's native process handle with the matching incarnation.

Registration bounds are now explicit: version 1 has a 64-byte header and at most
4096 bytes of strict UTF-8, BinaryWriter length-prefixed run/scenario strings.
Each identity is nonblank, not `(none)`, at most 1024 UTF-16 code units, and contains
no NUL, CR or LF. Invalid surrogate input and aggregate overflow are rejected.
The reader compares exact retained registration bytes and all defined/reserved
header fields; packet lengths do not control its allocation. These registration
bounds do not settle payload catalog, aggregate payload bytes or preparation cost.

The endpoint admits one successful attachment, one completion attempt and one
parent take. Mutex acquisition is nonblocking. A failed/contended completion
attempt cannot later retry. Parent cancellation/death, admitted lease abandonment,
early take, wrong scope/incarnation and changed metadata prevent a usable result.
The parent only issues an attachment completion after its held app handle reports
exit code zero and the exact completed request is still unrevoked. Request/lease
cleanup releases their native handles; no payload file is created or discovered.

`AttachmentCompletion` has no public constructor/setters and is a distinct local
receipt, not a publication completion. `LiveProofEligible`,
`NativeWriterCustodyEstablished` and `PublicationCompletionIssued` remain false.
It does not prove the original GUI request, exact producer preparation, writer,
source file, QPC-domain identity, payload persistence or primary custody. This is
trusted local workflow custody, not a hostile same-account security boundary.

Fresh real PowerShell child processes exercise the app role on both Windows
PowerShell 5.1 and PowerShell 7: 23 scenarios per host plus real native parent-death
checks, registration limits and exited-before-bind controls. The final four test
rows passed. The nLink GUI itself does not yet load or activate this codec, and
the fixtures are not live GUI/NKN acceptance tests. See
`artifacts/frontier-native-app-20260915/RESULTS.md` for retained evidence, including
the corrected initial test-barrier failure.

Next, settle bounded producer admission/catalog/work rules and connect this native
role to the exact private preparation and operational writer. The original GUI
request/app launch link, persisted completion/marker ordering, payload/source read
custody and separate primary/parent channel remain unimplemented. Existing GUI
and primary native descriptors/issuers were not edited by A8b1. No live rerun or
correctness reclassification follows from this attachment-only result.

### A8b2: private producer catalog bounds (implemented)

The next isolated catalog owns admission before capture begins: it creates each
private observation itself and accepts preparation only from those exact retained
objects. It has no import of existing observations, payload bytes, claimed hashes,
writer IDs or caller-defined limits. Native request and writer binding remain
separate links; creating this catalog alone does not enable capture in the app.
An admitted observation retains its catalog owner and refuses the old standalone
preparation method or a foreign catalog, including before its first preparation.
Standalone A8a observations continue using their original ownerless private path.

Limits are fixed diagnostic policy, independent of the 64 physical-witness cap:

- At most 16 lifetime admissions per catalog, counted when created, not when an
  observation succeeds. Invalid, abandoned or failed observations never free slots.
- At most 262144 UTF-8 bytes per prepared payload and 4194304 retained payload bytes
  in total. This is a payload-byte bound, not a claim about all CLR allocations.
- At most one preparation attempt per admitted observation and one in flight for
  the catalog. Validation/serialization stays outside the service and catalog
  gates. Existing 64-entry lifecycle and 128-range limits remain unchanged.
- A two-second cumulative elapsed preparation budget is checked before and after
  work. It is a cooperative cutoff, not thread abortion or a hard latency bound:
  an in-flight preparation may finish after the cutoff, but its result is discarded
  and no further catalog work is admitted. Existing intermediate serialization
  allocation/cost still needs end-to-end verification before runtime integration.
- One complete take, only when every admission has its exact positive preparation.
  Empty/early/partial take, capacity failure, failed preparation, overlapping
  preparation or work-budget exhaustion invalidates the whole optional catalog.
  No partial-success selection, newest-success replacement, retry or eviction.
- Cancellation/disposal releases catalog-held producer/payload references and
  revokes even a previously issued private snapshot. It does not mutate runtime
  observations or send settlement; existing producer caches may remain owned by
  their original contexts. Copied diagnostic bytes never confer ownership.

Native writer integration must not treat `OrderedOperationalLogWritePump`'s
`lastPersistedSequence` or `FlushThroughSequence` as an exact-batch persistence
receipt. Current source advances that high-water mark after a successful later
write even when an earlier batch failed. The new marker/source contract needs
exact batch identity and success evidence, plus original accepted-completion and
drain ordering. This is a source-level integration constraint, not a new live-run
RCA or authorization to change the existing log pump in this catalog-only step.

`SessionFileTransferService.FrontierPayloadCatalog.cs` implements this policy as a
private in-memory component. Its one-shot `PreparedCatalog` snapshot remains
revocable and copies bytes only while the original owner still recognizes that
exact snapshot. Equal-count/byte-count wrappers and foreign catalogs cannot claim
ownership. Cancellation does not call back into the runtime producer or alter its
captured clocks, outcome or cached preparation.

The catalog is test-instantiated only. It is not yet registered by A8b1's native
app lease, does not establish a writer ID or exact persisted completion, and does
not write files or issue a native output descriptor. Every snapshot still has
`LiveProofEligible=false`, `NativeWriterCustodyEstablished=false` and
`PublicationCompletionIssued=false`. See
`artifacts/frontier-payload-catalog-20260915/RESULTS.md` for focused verification.

Next is the native request-to-catalog and exact operational-writer ownership
binding, including a genuine exact-batch persistence receipt. Keep capture/export
activation disabled while closing those links. GUI launch ownership, persisted
marker/drain ordering, native file/read custody, primary/parent integration and
end-to-end cost/failure containment remain later gates. A cooperative preparation
cutoff is not evidence that runtime integration meets a hard latency bound.

### A8b3: native request-to-Core catalog binding (implemented)

Core now compiles the existing `LocalFrontierAppRequest.cs` source through a
project link. The PowerShell parent and managed app role therefore use the same
wire implementation; there is no second codec, changed header, new output format
or new package dependency. Merely loading Core does not open a native request.
Conditional Windows platform metadata and file-local numeric warning pragmas
preserve the legacy compiler source while allowing a zero-warning Core build;
Core's project-wide nullable settings remain enabled. A rejected intermediate
`#nullable` approach is recorded in the verification artifacts.

The admitted app lease has one lifetime catalog-claim attempt. It retains the
exact opaque owner reference, checks the native request before and after binding,
and clears a failed raced binding. A foreign opaque object may consume that slot
but cannot become a Core producer or output capability. The private Core factory
creates its own catalog and claims the slot with that exact object. Null, expired
or already-claimed leases cannot create another native-bound catalog.

Native-bound catalogs check the original live lease before admission/preparation,
after preparation, before take and on every snapshot ownership/copy operation.
Admission also compares the observation's actual captured process/incarnation to
the bound app. Parent death, cancellation, header/registration mutation, lease
disposal or any attachment-completion attempt revoke catalog access and discard
retained output on the next check. No background callback mutates a runtime
observation. Already copied bytes are diagnostic data, not continuing custody.
The 16-admission, byte, one-shot and cooperative work limits remain unchanged.

Standalone A8a/A8b2 fixtures still exist, but are not native-bound catalogs. Their
private ownership must never be accepted as native writer proof. All snapshot and
native attachment live/writer/publication flags remain false. Completing the
attachment revokes live catalog access; that receipt cannot preserve or relabel
catalog bytes as a completed payload/source descriptor.

Fresh Windows PowerShell 5.1 and PowerShell 7 parents launch real PowerShell 7
app-role processes that load the built Core and service-fixture assemblies. Tests
assert the native type comes from Core, prepare actual V6/V4-compatible producers
before source-stream closure, and compare exact bytes/process identities. Native
revocation is exercised before creation/preparation, during blocked preparation,
after preparation/take, and by genuine parent exit without managed cancellation.
These are real-process Core integration tests, not nLink GUI/live acceptance.
See `artifacts/frontier-native-catalog-20260915/RESULTS.md` for final verification.

The private factory is called only by tests. App startup, GUI launch/finalization,
operational writer and primary/parent channels are not wired to it. Next is exact
operational-writer registration and persistence evidence. In particular, the
current Coordinator emitter queues only message strings and `OperationalLogWriteBatch`
is a copyable record struct. Neither is an unforgeable receipt for the original
accepted completion. Preserve that original envelope association through the
optional writer registration, and issue exact private batch-success evidence from
the owning writer; retain bounds, failure containment and unchanged queue policy.
Then bind marker/drain ordering and native payload/source read custody. No live
activation, acceptance change or historical reclassification follows from A8b3.

### A8b4: bounded exact writer-submission receipts (implemented primitive)

The operational pump now has an optional `SubmitWithReceipt` path. Under its
existing gate it creates an opaque token for that one submission and retains the
token by reference in its own private registry. A queued batch carries that exact
token through the existing FIFO worker. Equal copied batch values, equal sequence
numbers, caller-constructed tokens, foreign pumps and later high-water success
cannot establish ownership or upgrade another submission's outcome.

The diagnostic limit is 64 lifetime receipts per pump, separately defined from
the 16-producer catalog and 64-physical-witness limits. Pending, successful and
failed submissions all consume their slots permanently; there is no recycling,
eviction or replacement. The registry retains only tokens and enum states, not
batch text, payload bytes, producer objects or callbacks. Ordinary `Submit` calls
consume no slots. At capacity, after receipt revocation, or after writer
abandonment, the log submission still follows its existing policy but returns no
new receipt. A future integration must omit optional output if its required
receipt is absent, not retry or repurpose another successful token.

Owned state starts Pending. Only the exact immediate write or the exact retained
queued batch's successful append changes it to Persisted. Immediate rejection,
queue-capacity rejection or terminal I/O failure changes it to Failed. Shutdown
abandonment changes pending receipts to Abandoned before late worker completion
can be considered. Late physical bytes cannot upgrade an abandoned receipt.
Permanent pump-wide diagnostic revocation makes every owned token report Revoked
and stops further issuance without cancelling, retrying or suppressing log writes.
Null, manufactured and foreign tokens report Unowned even after revocation.

Persisted means that this pump observed `RollingFileLogger.TryWriteLine` succeed.
It is not an fsync/power-loss durability guarantee, proof of retained file identity,
or protection against later mutation/rotation. Failed/Abandoned likewise does not
prove physical bytes are absent. Outcome reads may repeat; they are not one-shot
native publication capabilities. Existing high-water flush semantics, queue and
byte caps, coalescing limit, retry policy, marker format and logging return values
remain unchanged. No new write, wait, callback or producer serialization is added
to the diagnostic path.

Focused verification passed 27/27: 17 exact-receipt rows and 10 existing logger
integrity rows. Real exclusive file handles reproduce the earlier-failure/later-
flush gap and terminal I/O failure. Named mutexes cover deferred FIFO persistence;
test-only reflection holds the existing logger gate to prove a physical append
after shutdown abandonment cannot become positive receipt evidence. Capacity,
concurrent issuance, foreign tokens, equal batch copies, completion rejection,
revocation and throwing marker sinks are covered. Core builds with zero warnings
and errors. See `artifacts/frontier-exact-batch-20260915/RESULTS.md`.

Only tests invoke the new receipt API. `LocalOperationalLog` and the Coordinator
emitter are not yet wired to it: neither the actual private writer identity nor
the original accepted-completion envelope is bound to a native catalog. The next
step is that exact optional association, preserving the original envelope before
the emitter reduces it to a string and obtaining the receipt only from the real
writer's submission. Marker/drain ordering, native payload/source file custody,
GUI and primary/parent integration remain later gates. Capture/export activation
and all live/writer/publication eligibility flags remain unchanged and false. No
portable rebuild, live-256 run, commit, push or historical reclassification follows
from this write-outcome primitive.

### A8b5: native catalog-to-original completion writer binding (implemented)

A native catalog may explicitly bind the operational writer once, before its
first producer admission. The real `LocalOperationalLog` registers that exact
catalog/lease pair once per process, checks the current process/incarnation
against the admitted native app, and supplies its own private writer identity
and batch metadata. A standalone catalog, late/repeated bind, foreign writer
registration, revoked lease or mismatched run/scenario cannot establish the link.
No caller-supplied writer ID, sequence, batch hash or PID-derived identity is used.

Each admitted producer gets one private completion binding. Immediately before
the existing inbound completion decision is adopted/enqueued, the observer checks
the original frame reference and delivery token against that accepted decision
and stages its original envelope reference. This does not move the existing
completion clock read, add an authority evaluation, or change send settlement.
The service-gate portion only stages/matches managed references; it does not check
native handles, perform logging, wait for persistence or serialize another payload.

The emitter associates that same envelope with its actual queued message object.
Its existing `ConcurrentQueue<string>` and serialized text remain unchanged. A
bounded side association uses reference identity for the exact string object;
equal copied text, parsed IDs/hashes or a copied envelope cannot recover it. This
preserves existing proof fixtures/consumers that directly drain the string queue.
Only the native catalog's at-most-16 admitted completion bindings enter the side
association, and the normal background drain removes each when it writes that
message. No completion-message search or retrospective reconstruction is added.

During that existing drain, `LocalOperationalLog` submits its own freshly created
batch through A8b4 and privately associates the exact returned token with the
original completion binding. The registration allows 16 lifetime completion
write attempts; pending, successful and failed attempts do not recycle slots.
Duplicate associations cannot replace the first result. Overflow revokes the
optional registration and releases its retained associations while ordinary
logging continues. This is a completion-only budget, separate from the pump's
64 receipt slots and from future marker/drain receipts.

Receipt metadata contains the actual writer ID, generation, sequence range,
attempt ticks, byte count and hash, not a retained copy of the log text. A copied
receipt wrapper is not owned. Reads require the exact registered wrapper and
association, a still-current native catalog/lease and run/scenario, and the pump's
exact Persisted outcome. Native cancellation, parent death, header mutation,
lease disposal or attachment completion invalidate access on the next check;
catalog cancellation also permanently revokes its writer registration. This does
not cancel queued log writes or grant any runtime authority.

The prepared catalog exposes a completion receipt only for its own admitted,
positively prepared producer and that producer's original accepted envelope.
Preparation/take remain payload operations: a taken payload snapshot is not a
completed writer/source descriptor. Missing, pending, failed or revoked receipt
access returns no writer proof. The receipt still proves an observed successful
append, not fsync durability or immutable source-file custody.

Fresh native app-role fixtures on PowerShell 5.1 and 7 cover the real service
producer, queue, operational logger, exact line bytes/hash and native revocation.
They include deferred/failed writes, copied envelopes/messages/receipt wrappers,
foreign registrations, repeated association, registration ordering and completion
capacity. Existing catalog, logger, runtime noninterference and affected proof
consumer checks are retained in
`artifacts/frontier-native-writer-20260915/RESULTS.md`.

Binding is explicit and invoked only by fixtures; app startup and GUI launch do
not opt in. Payload/native attachment live eligibility, full native writer/source
custody and publication-completion flags remain false. Next is the digest-only
payload marker after the exact completion and before an exact writer drain, then
native payload/source file identities and read custody. GUI/primary/parent wiring,
end-to-end cost/failure containment and correctness approval remain later gates.
No live-256 rerun, portable rebuild, commit, push or historical reclassification
is authorized by this link alone.

The wider A8b5 compatibility check also exposed pre-existing bootstrap catalog
coverage failures: a stale profile assertion and a production source-hold selector
that omits newer supported catalogs. These are not permission to weaken proof
requirements. Resolve the separate
[RCA and bounded fix plan](file-transfer-bootstrap-catalog-coverage-rca.md) before
considering that wider gate green or advancing to marker/drain implementation.

2026-09-16: the bounded catalog-coverage fix is implemented and its 12 distinct
targeted cases have passing latest results across the retained focused runs; see
`artifacts/bootstrap-catalog-coverage-20260916/RESULTS.md`. The eight known failures
are resolved without rerunning or rewriting the historical 58/66 artifact. This
unblocks the separately contracted marker/drain work, not live activation or full
native writer/source custody. No new production evaluation or runtime authority
was added.

### A8b6: exact completion / digest marker / drain ordering (implemented)

2026-09-16. An owned, taken catalog can explicitly attempt marker submission once,
outside the service gate. Every admitted producer must already have its original
positive preparation and an exact persisted completion receipt. Missing completion
revokes optional catalog access; no polling, replacement producer or later-success
selection is performed by submission. Returning true means submitted, not persisted.

Each `filetransfer_frontier_payload_marker` contains only the fixed producer format
and hash domain, original observation ID, prepared byte count and SHA-256, and the
original completion's writer batch generation, sequence range and physical-line
SHA-256. Normal writer metadata supplies the writer ID and marker sequence. The
marker contains no payload bytes, raw source IDs, paths or runtime tokens. Its
payload digest comes from the retained original preparation, not a caller-supplied
digest or parsed log. The payload still has no input-log hash or marker self-reference.
The redactor preserves only exact event-scoped digest/observation/domain fields.

The operational writer keeps a separate at-most-16 marker-receipt registry, keyed
by the exact original completion receipt, with no replacement or recycling. A
duplicate/conflicting marker association revokes optional writer evidence. Marker
and completion receipt roles are distinct: even a genuinely persisted completion
receipt associated with another completion cannot serve as a marker receipt.
The catalog itself has no marker-message or marker-receipt import API.

The existing final drain can now optionally obtain its own pump-issued exact
receipt. Its existing high-water flush boolean and ordinary completion behavior
are unchanged. Repeated completion does not issue a replacement drain receipt.
Failed, pending, abandoned, revoked or absent exact drain evidence cannot be
upgraded by later high-water success. The combined maximum is 16 completion + 16
marker + 1 drain receipts, within the existing 64-receipt pump limit. Exhaustion
omits diagnostic evidence without suppressing normal drain logging.

The catalog exposes an original-reference-owned `OrderedWriterEvidence` only
after every admitted payload's completion and marker, plus the exact final drain,
are persisted by that native registration. Batch and sequence ordering must be
completion < marker < drain. A later writer attempt invalidates this final-drain
ordering result even if the ordinary write is rejected. Copied catalog/order/
receipt wrappers, missing markers and partial catalogs cannot establish custody.
Native cancellation, metadata changes, parent death, lease/attachment closure or
catalog cancellation invalidate reads, including previously returned order objects.

This is append-order evidence only: no fsync guarantee, native payload output,
retained file identity/read lease, GUI ownership or primary/parent capability is
issued. All live/full-writer-custody/publication-completion flags remain false.
App/GUI activation is still absent. No new authority evaluation, service-gate I/O,
payload serialization under that gate, boundary-clock sampling, route mutation or queue/
retry policy change was added. Marker formatting and native checks are bounded
by the existing catalog count; hard end-to-end export latency remains a later gate.

See `artifacts/frontier-native-marker-20260916/RESULTS.md` for exact-drain,
redaction, real native-process and affected compatibility verification. Next is
the separately versioned native payload/source-file descriptor and retained read
custody, including mutation/partial-output rejection and original GUI/app ownership.
No live-256 test, portable rebuild, commit, push or historical reclassification.

### A8b7: original app-writer source identity and read custody (implemented)

2026-09-16. The preceding append receipts alone did not identify the native file
that actually received each write. Opening the configured path after drain could
select a replacement. The optional native writer path now records the volume/file
identity and exact physical UTF-8 byte range at the actual append handle. These
scalars are diagnostic; only the pump's original reference-owned receipt registry
binds them to a submission. Ordinary logging and receipt capacity behavior remain
unchanged. BOM, newlines and multiline UTF-8 bytes match ordinary logging.

Only the original `CompleteAndFlush` invocation can request retained source
custody. Its actual drain append reopens its native handle for reading, closes the
append handle, then tightens the retained handle to read-only sharing. Neither a
path reopen nor a caller-provided digest can establish custody. A generic batch
with `IsDrain` set cannot issue it. Deferred/coalesced writes retain their individual
physical ranges, including real separators; failed, abandoned or revoked writes
cannot acquire custody even if a late physical append succeeds. Rotation or path
replacement between selected writes rejects the source identity chain.

An owned, taken catalog can attempt source acquisition once. All admitted original
completions and markers must already have exact final-drain ordering. A single
bounded traversal of the retained handle checks every selected raw byte range and
computes the complete source length/hash. The limits are 33 ranges (16 completions,
16 markers, one drain), 256 MiB of source bytes, a 32 KiB read buffer, and the
existing five-second cooperative verification budget. This source limit is not
the catalog payload limit. The elapsed checks do not claim a hard wall-clock
deadline for synchronous filesystem calls. No authority evaluation is added.
The file traversal runs outside both operational-writer and catalog/service gates.

The resulting app-local source object is reference-owned by the original catalog.
Copied wrappers, copied/foreign pump receipts, partial catalogs, wrong file identity,
changed selected bytes, repeated acquisition, or failed native post-checks cannot
establish custody. Read handles are reopened only from the retained native handle;
normal writes/rename/delete are denied while it is held. Catalog disposal, observed
native revocation, failed verification and post-drain writer attempts retire custody
and release the retained handle. Each eligibility/read-opening check rechecks native
ownership. Previously copied bytes or an already-open stream are not continuing
custody capabilities; trusted same-account code is still the threat boundary.

This does **not** issue the separately versioned cross-process source descriptor,
write a native payload output, connect GUI/app launch ownership, or enable the
primary/parent channel. All live/full-writer-custody/publication-completion flags
remain false. App/GUI activation is absent. The next gate is native payload output
and the versioned descriptor/handoff contract, including partial-output rejection.
See `artifacts/frontier-native-source-20260916/RESULTS.md` for focused verification.
No live-256 run, portable rebuild, commit, push or historical reclassification.

### A8b8: original catalog-owned native payload files (implemented)

2026-09-16. The original taken native catalog can explicitly attempt
`TryWritePayloadFiles` once, after acquiring A8b7 source custody. There is no payload,
path, hash, writer-ID or file-descriptor import argument. The catalog copies only
its original admitted preparations, in admission order, and compares each native
file's verified length/hash to that preparation's retained length/hash. Existing
source custody rechecks all original completion/marker/drain links and native
request ownership before, during and after output. Copied snapshots cannot consume
the original attempt; repeated attempts cannot recreate or replace files.

The existing bounds remain 16 files, 262144 bytes per payload and 4194304 payload
bytes in aggregate, independently of the 64-witness limit. The writer uses the
same per-payload constant as admission. One five-second cumulative, cooperative
output budget covers copying, writing and verification of the whole catalog,
separate from preparation and source-verification budgets. Expiry omits all output;
synchronous filesystem operations are not forcibly interrupted and this is not
a hard end-to-end latency guarantee. No serialization or authority evaluation is
added; exact canonical prepared bytes, including their BOM-free encoding, are used.
All byte copying/writing/hashing runs outside catalog/service gates.

Files use fresh random names and native `CREATE_NEW` in the operational writer's
existing log directory. A pathname is only a creation location, never evidence of
ownership or a way to rediscover output. Native delete-on-close applies from file
creation, including failed creation work and abrupt process exit. The actual writer
handle supplies identity and length; a retained read-only native handle verifies
the exact bytes/hash and denies writes. Reads reopen this native handle, not a path.
On this Windows workflow the pending-delete file also rejects pathname opening,
rename and replacement. Cleanup closes owned native handles; it does not delete
whatever later occupies a reused path. An already-open diagnostic reader can delay
physical deletion until it closes, but cannot preserve catalog eligibility.

The catalog exposes one original-reference-owned `PayloadFilesEvidence` only after
every output succeeds. It supplies bounded indexed native reads and diagnostic
identity/length/hash, and the original source and ordered-writer evidence. Wrong
indexes and copied wrappers cannot open reads. Missing source, an output failure,
digest mismatch, budget failure or raced revocation destroys the entire optional
output and invalidates its catalog. No successful prefix, retry, newest-success
selection or fallback path is accepted. Disposal and observed native/source
revocation retire all owned payload handles without changing runtime decisions.

These are **temporary app-local native files**, not a cross-process completion.
Delete-on-close deliberately prevents post-exit pathname recovery. The later handoff
must transfer native read custody to the original GUI/app request roles before app
custody ends, using a separately versioned bounded descriptor. No descriptor/wire
extension, GUI launcher integration, primary/parent capability or activation is
implemented here. All live/full-writer-custody/publication-completion flags remain
false. No old channel format, issuer or historical acceptance result is changed.
See `artifacts/frontier-native-payload-files-20260916/RESULTS.md` for verification.

### A8b9: early original GUI launch ownership and shared-source handoff revision

2026-09-16. Integration inspection found that both Helper and Sharer receive the
same append-only acceptance-log path. A8b7's per-app final-drain freeze denies the
other app's remaining writes, including before explicit source acquisition. A8b8
then requires that frozen source before emitting its temporary payload files.
The existing GUI writer issuer attaches only in finalization, too late to own
provisional app output from launch. These are unactivated integration blockers,
not evidence that a historical live run failed at this edge. See
`file-transfer-frontier-shared-source-handoff-rca.md` for the source evidence and
bounded fix sequence. The prior app-local results remain valid in their isolated
scope; they do not validate shared-log activation.

`LocalGuiWriterRequest.TryAttachFrontierLauncher` now supplies a separate one-shot
early owner for the original normal GUI writer request. It verifies the exact
request ID, registration bytes, GUI PID/incarnation and live original native parent
handle. It neither consumes the old finalizer's attachment slot nor changes the
old request/completion bytes. The owner remains current at original stages 0/1,
and retires when completion writing starts, the stage closes, metadata changes,
the parent cancels/exits, or the owner is disposed. It is not a relay issuer.

Only the original owner's binding path can create app-launch evidence. It
duplicates the supplied actual process handle and obtains PID, creation time and
exit evidence from that retained handle. This lane admits exactly two distinct
live app processes, matching Helper + Sharer. A failed binding, duplicate, third
launch, bind after sealing or incomplete seal retires this optional scope. It
does not evict an earlier app, retry binding, terminate apps, or cancel the old GUI
finalizer. The trusted launcher must pass its actual launch objects; this is not
an OS-parent attestation or a hostile same-account security boundary.

The app set must be sealed before `AllAppsExitedSuccessfully` can report both
original processes exited with code zero. Original-reference membership rejects
copied owner/app wrappers; a copied owner's disposal cannot close original handles.
This is process-exit evidence only: it proves neither exact writer drain nor
stdout/stderr EOF, retained file custody, source sealing or acceptance success.
All live/full-writer-custody/publication-completion flags remain false. The new
entry point has no production caller; app/GUI activation remains absent.

The cross-process handoff must use the following two-phase sequence:

1. After the original parent completes `BindWorker`, and before app launch,
   establish this original GUI owner; bind every actual app process and seal the
   bounded launch set. The future startup integration must supply the original
   parent-binding readiness gate; a premature failed attach cannot retry.
   A8b10 below now joins each binding to its original native app request. Its
   original catalog/file contribution still needs the separately versioned bounded
   handoff channel. Matching scalar PID/request/writer strings cannot reconstruct
   either ownership link.
2. While each app is alive, transfer provisional native append/source anchors,
   exact completion/marker/drain receipt evidence, and original prepared payload
   handles into GUI-owned custody. Do not tighten shared-source sharing at any
   single app's drain. The source may still grow while other registered writers
   run; provisional descriptors are not final hashes or publication capabilities.
   A8b8's frozen-source-dependent entry point cannot simply be activated for this
   shared-log flow. The next bounded implementation must separate provisional
   handle retention/output from final source verification without weakening the
   existing isolated app-local custody checks.
3. Only after every registered app's exact drain, successful original native exit,
   and original stdout/stderr EOF/output gates may the original GUI finalization
   path seal the retained shared source. Seal from a retained native anchor, never
   by reopening a path. Then validate identity, every selected original byte range,
   final length/hash and all original payload/completion/marker links. Recheck the
   original GUI request and app bindings through handoff/finalization. Partial
   handoff, unknown writers, replacement, mutation, failed exit, missing drain/EOF,
   cancellation or budget expiry omit the entire optional result.
4. Publish a distinct bounded native GUI handoff only from that successful sealed
   state and the original finalizer gates, before the unchanged legacy completion
   closes its request. A separate primary/parent capability and their original
   exit/input/terminal gates remain A8c work. No JSON, pathname or success boolean
   can substitute for original native/reference custody.

The two phases are provisional app-to-GUI custody while writers live, then one
GUI-owned final shared-source seal after all writers finish. Existing per-catalog
payload/source caps and the primary witness cap still apply. The launch-only A8b9
primitive did not settle aggregate GUI descriptor, selected-range, byte and work
limits or implicitly multiply the old limits; A8b10 below now specifies them
before transfer implementation. No full-log polling, extra authority
evaluation, runtime route/retry change or additional boundary-clock sample is
authorized. Per-app logs, transport tuning and old-completion schema changes are
not part of this fix. Tests/results are retained in
`artifacts/frontier-gui-launch-20260916/RESULTS.md`.

### A8b10: original GUI-owned app-request join (implemented; file handoff remains later)

2026-09-16. The original early GUI owner can now create at most two native app
requests before their respective launches. `TryCreateAppRequest()` accepts no
request, endpoint, PID, run/scenario, catalog or descriptor import. It uses only
the original retained GUI run/scenario and owns the real `LocalFrontierAppRequest`
objects in a private lifetime registry. Their existing 4096-byte registration cap
still applies; failure retires optional diagnostics without changing the GUI run.

Each original `FrontierAppRequest.BindApp(Process)` binds the same actual process
to both the GUI launch registry and the original native app request. Both retained
handles independently supply native PID/incarnation; the GUI owner rechecks the
app request's exact header/registration and original GUI parent incarnation on
every currentness check. The Core-loaded app still attaches with the unchanged
version-1 app codec and its original native parent-handle check. Endpoint names
and request IDs only locate that request; copying them cannot import a GUI join.

The request set has exactly two slots, no eviction/retry, and must be fully bound
before sealing. A third request, duplicate binding/process, request allocation
after sealing, incomplete set or mixing request-owned and plain launches retires
the optional GUI scope. A copied request wrapper cannot bind, dispose or claim the
original. Disposing one original request abandons the entire optional set, closes
its owned handles and cancels its native app leases; it does not terminate apps
or signal the legacy GUI writer request's closed event. Observed native app
cancellation/metadata changes, or original GUI/parent revocation, do the same.

`AllAppRequestsCompletedSuccessfully` requires the original sealed two-request
set, both original native app-attachment completions and both successful native
process exits. Missing attachment cannot be upgraded by exit code zero. This is
still attachment/process evidence, not exact writer drain, output EOF, catalog
publication or file custody. `AllAppsExitedSuccessfully` remains explicitly the
weaker process-only observation. Every live/full-custody/publication flag remains
false. No production caller, file transfer or source freeze was added.

The PowerShell loaders now compile GUI and app request ownership in one bundle
so their internal original objects share one type identity. Either script-loader
order is supported. A managed app with Core already loaded keeps Core's identical
app codec. Trying to add the tool ownership bundle over a separately loaded app
codec is rejected, rather than loading an ambiguous second type. Neither existing
GUI request/completion bytes nor version-1 app wire fields/states were extended.
The separately versioned provisional-file descriptor remains a later codec.

#### Explicit aggregate limits for the later provisional handoff

These are the selected two-app handoff policy, not new limits on the existing
per-app catalog and not a claim that this request-only step transfers files:

| Resource | Per original app | One original GUI request |
| --- | --- | --- |
| Native app requests | 1 | Exactly 2 registered writers |
| Payload admissions/files | At most 16 | At most 32, no slot recycling |
| Payload bytes | 262144 per file; 4194304 total | 8388608 total |
| Exact completion/marker/drain ranges | At most 33 | At most 66 |
| Physical source bytes | Existing 268435456 cap | One shared source, same cap, not doubled |

The 66 range bound is two independent 16-completion + 16-marker + one-drain
chains, not permission to increase the primary's separate 64-witness limit.
There may be fewer payloads, but every registered writer still needs its original
drain and custody. A writer with no admitted positive payload must use an explicit
writer-only handoff variant; it cannot fabricate a positive empty catalog or be
dropped from source sealing. That variant and its original writer binding remain
part of the forthcoming descriptor/producer implementation.

The new descriptor channel must cap encoded metadata at 65536 bytes per GUI
request, including both apps' identity/range/payload metadata. Allocate from fixed
local bounds, not untrusted wire lengths. Retain at most 34 provisional file
handles: 32 payloads plus two source anchors identifying the same shared source.
During final verification, at most one additional sealed-source handle and one
additional read handle may be open; do not open a reader per range or payload.
Native process/control handles are separately bounded by the two-request registry.

Use one nonblocking submission and one adoption attempt per original app, with
all-or-none catalog adoption and no partial-prefix salvage. No waiting under
service, catalog or logging gates. Each app keeps its existing cumulative
preparation/output bounds; the GUI gets one five-second cumulative cooperative
adoption/verification work budget across both apps, excluding idle time while
the existing run waits for apps. No resetting that budget per file, range or app.
The run's existing outer deadline still applies; synchronous native filesystem
calls are not forcibly aborted, so this is not a hard wall-clock guarantee.

No provisional source hash is a final hash. All files/receipts must be held from
their actual original handles while apps live; the shared source remains writable
until every app's exact drain, successful exit and original output EOF gates.
Only then may GUI-owned sealing and one bounded whole-source traversal verify all
selected byte ranges. Failure/overflow/cancellation omits the complete optional
output without affecting runtime or old completion behavior. Before coding that
codec, specify its exact state transitions and handle-ownership rollback; the
limits above do not themselves establish cross-process custody.

The unchanged app attachment lease alone cannot opt the writer into provisional
shared-source behavior: standalone app requests use the same version-1 codec.
The forthcoming versioned handoff must bind the original Core lease/catalog to
this original GUI-owned request before selecting that writer path. Do not add an
environment boolean or caller-authored mode that bypasses the isolated A8b7 seal.
Provisional adoption must finish while the original Core lease/catalog is still
current, before any `CompleteAttachment` attempt retires app-local catalog access.
The GUI's later successful attachment/exit observation cannot resurrect a retired
catalog or recover delete-on-close output by pathname.

Verification for the current join is retained in
`artifacts/frontier-gui-app-join-20260916/RESULTS.md`. The next implementation is
provisional source retention without a per-app shared-source freeze, followed by
the bounded descriptor/handle adoption and final GUI seal. App/GUI activation,
primary/parent custody and end-to-end correctness/cost review remain later gates.

### A8b11: native provisional source anchor (implemented; no writer opt-in)

2026-09-16. `OperationalLogNativeAnchor` is an internal file primitive that retains
the native source without freezing it. Its only creation input is an already-open
native file handle; it accepts no pathname, claimed identity, hash or length. It
records actual volume/file identity and a capture-time byte count, then reopens
that handle with read-only access and read/write/delete sharing. The original
caller handle remains owned by its caller. Native reads also reopen only the
retained handle with the same sharing. No path is rediscovered during retention,
reading or cleanup.

Delete sharing is deliberate. Review and a new failing regression showed that
read/write sharing alone still blocks another logger's delete or rename rotation.
Both rotation variants now succeed while an anchor and reader are held. Rotation
can invalidate the later all-writer identity chain; the optional handoff must then
be omitted, not suppress ordinary logging or silently combine different files.
The existing A8b7 frozen-source path retains its strict read-only sharing unchanged.

Retention admits regular disk files only, with an initial size from 1 through
268435456 bytes. Closed/invalid/exclusive input handles, directories, devices and
pipes are rejected without closing the caller's handle. Subsequent retention/read
checks require the same native identity, at least the capture-time byte count and
at most the existing source cap. Observed truncation/overflow permanently retires
the anchor, even if the file is later restored. Copied wrappers cannot read or
dispose the original. Disposal closes only owned handles, never deletes a pathname
or touches a later replacement. Already-open readers own their separate handles
and cannot preserve catalog eligibility after their owner is revoked.

`CapturedByteCount` is not a final length, and `IsRetained` is not byte-integrity or
producer-custody proof. Same-length mutation can remain retained and appear in a
provisional read. There is no hash, seal or publication operation on this primitive;
all original selected-range hashes and the final whole-source hash must still be
checked after the future GUI-owned seal. Bytes may grow after a read opens; the
future channel must enforce its own bounded traversal and pre/post checks.

This primitive is not called by the operational pump, catalog, app startup or GUI.
No alternate capture mode, fallback after failed freezing, environment opt-in or
source-retaining drain was wired in. The original GUI/app handoff capability must
authorize that future integration; a standalone app lease or arbitrary retained
handle cannot do so. The only existing helper change exposes native file attributes
through an overload; the prior identity/length API and its behavior are preserved.

#### Ownership and rollback rules for the next native channel

The channel must use GUI-side pull duplication from the already-held original app
process handle. Do not have apps push handles into the GUI before publishing their
values: app death between remote duplication and recording the value can strand
an untracked handle in a still-running GUI. No numeric handle from a descriptor
may be wrapped as an already-owned GUI-local handle, and no rollback may close a
remote app handle. Each process closes only the handles it actually owns.

| Phase | Original app owns | Original GUI owns | Required transition |
| --- | --- | --- | --- |
| Registered | Original live Core lease/catalog or explicit writer-only registration | Original A8b10 request/launch set and separate versioned channel | Exact roles, scope and bounds checked |
| Prepared/submitted | Immutable bounded manifest plus its original payload handles and writable source anchor | No adopted files | One complete submission after exact local writer evidence; no partial prefix |
| Adopting | All original handles remain live; attachment is not completed | Locally duplicated provisional handles, not yet published | Duplicate from the original native app process, then validate every identity/length/payload digest and receipt binding |
| Adopted | May release originals only after exact original acknowledgment | Complete, reference-owned provisional set | All-or-none acknowledgment before app lease/catalog retirement |
| Writers closed | Original attachment completed; process exited successfully | Both original sets plus exact drains and original output EOF evidence | Every registered writer accounted for, including writer-only contributions |
| Sealed/finalized | No app authority recreated | One sealed shared source and verified complete output | One bounded final range/whole-source verification, then separate original finalizer/primary/parent gates |

Any missing/mutated/partial descriptor, failed duplication, expired bound, native
revocation or unexpected app exit rejects the complete optional result. GUI closes
all its local duplicates; app closes its originals on failure/cancellation. Neither
side performs pathname cleanup, rescans for a replacement, retries adoption, or
reconstructs authority from copied metadata. Acknowledgment is only provisional
custody transfer, not final source verification or live-proof eligibility.

The A8b10 budgets remain fixed. Preparation/adoption/acknowledgment must be outside
service/catalog/logging gates and fit existing lifecycle deadlines; this step adds
no acknowledgment wait or shutdown delay. Exact wire layout, readiness signaling
and bounded rendezvous integration must be reviewed with their implementation
before enabling a source-retaining writer path. A failed or missing acknowledgment
must omit diagnostics; later successful exit cannot resurrect app-local custody.

See `artifacts/frontier-provisional-source-20260916/RESULTS.md` for native file,
cross-process writer, rotation and affected compatibility tests. No live test,
portable rebuild, commit, push or historical acceptance reclassification occurred.

### A8b12: versioned file manifest and GUI-pull primitive (implemented; rendezvous pending)

2026-09-16. The internal `LocalFrontierFileManifest` and
`LocalFrontierFileAdoption` share one C# 5-compatible implementation between Core
and the PowerShell ownership bundle. They implement bounded wire decoding and
native file adoption, not the request-bound submission/readiness/acknowledgment
channel. There is no production caller. Their scalar scope, manifest bytes and
currentness callback are NOT original GUI/app/catalog/writer capabilities and
cannot authorize provisional writer capture, publication or a correctness verdict.

#### Exact manifest version 1

The format is `nlink-frontier-file-manifest-v1`. Integers are little-endian;
GUIDs use the existing .NET `Guid.ToByteArray()` representation. A manifest has
exactly `216 + writerUtf8Bytes + 316 * payloadCount` bytes, with no trailing data.

| Offset | Field |
| --- | --- |
| 0 | Int32 magic `0x4e464831` |
| 4 | Int32 version `1` |
| 8 | Int32 kind: `1` positive payloads, `2` explicit writer-only |
| 12 | Int32 payload count: 1..16 for kind 1, exactly 0 for kind 2 |
| 16 | Original GUI request GUID, 16 bytes |
| 32 | Original app request GUID, 16 bytes |
| 48 | Int32 app PID |
| 52 | Int32 reserved, must be zero |
| 56 | Int64 native app creation FILETIME |
| 64 | Int32 writer UTF-8 byte count, 1..128 |
| 68 | Exact strict UTF-8 writer identity, then source entry, drain range and payload entries |

A file entry is 28 bytes: positive Int64 remote handle value, UInt32 volume
serial, UInt32 file-index high, UInt32 file-index low, Int64 captured byte count.
The handle is a locator in the already-held app process, never a GUI-local handle.
Source length is 1..268435456. Payload length is 1..262144. No path is encoded.

An exact-range description is 120 bytes: seven Int64 values (batch generation,
first sequence, last sequence, attempt UTC ticks, batch UTF-8 byte count, native
offset, native span byte count), then 32-byte batch SHA-256 and 32-byte raw-span
SHA-256. A payload entry is 316 bytes: observation GUID, file entry, 32-byte
producer-payload SHA-256, completion range, marker range. The single drain range
is separate. The payload hash retains A8a's producer-v1 domain; batch and raw-span
hashes retain their existing distinct byte domains. There is no source final hash.

Decoding checks fixed bounds before allocating count/length-dependent arrays,
requires the exact encoded length, and rejects unknown versions/kinds, reserved
fields, malformed UTF-8, blank/control-character writer identities, empty request
or observation IDs, invalid handle/size values, duplicate handles/files/observations,
and source/payload aliasing. All selected ranges must fit the captured source,
be nonoverlapping, and respect increasing writer sequence and batch generation.
Each completion precedes its marker, and every marker precedes the one drain.
These are structural checks, not proof that a writer actually persisted a range.

The input cap is 32768 bytes per app / 65536 per GUI. This fixed version's
largest valid encoding is only 5400 bytes per app (128-byte writer and 16 payloads);
unused capacity is not extension space. File/range limits remain 16/33 per app
and 32/66 per GUI; payload-byte limits remain 4 MiB per app and 8 MiB total.
Writer-only decoding does not create an original writer registration.

#### Pull, verification and rollback

One primitive owns two fixed original-request slots. It snapshots and decodes the
complete manifest before any handle duplication, then checks the already-held
native app process's PID, creation time and still-live state. It never opens a
process by the descriptor PID. The consuming process cannot serve as its own app.
Two contributions must identify the same native source, distinct writer/process
identities, disjoint selected ranges and distinct payload files/observations.

For each file, GUI-side `DuplicateHandle` requests read-only access with neither
inheritance nor `DUPLICATE_CLOSE_SOURCE`. A local `SafeFileHandle` owns the duplicate
immediately. Native file type, identity and length are checked, and `ReOpenFile`
creates an independent read position. Source retention uses read/write/delete
sharing. Payload retention uses read/delete sharing to exclude writers before
hashing. Every payload is traversed once with a 32 KiB buffer and verified against
its exact bounded length/digest. Source content and selected-range hashes are NOT
verified at this provisional stage; same-length source mutation remains possible.

There are at most 34 retained provisional files. During pull, at most one extra
temporary duplicate overlaps its replacement retained handle (35 local file
handles at peak); it is closed before the next file. This explicitly accounts for
the working handle needed to change sharing/file position without path discovery.
No app is allowed to pre-duplicate a remote handle into the GUI. Later final
verification still has the separate one-sealed-source/one-reader limits from A8b10.

The same five-second cumulative cooperative stopwatch covers both adoptions and
retention checks, including pre/post currentness checks; idle time between calls
is excluded and the budget never resets per app/file. Native synchronous calls
are not forcibly aborted. The later final verifier must use this same budget,
not start another five-second allowance. No service/catalog/logging gate is used
and there is no poll loop, wait, endpoint, acknowledgment or shutdown delay here.

Any malformed/partial descriptor, failed duplicate/hash/currentness check, app
exit during adoption, replay, cross-app conflict or budget failure retires the
entire primitive, including a previously adopted first contribution. Cleanup closes
only local handles; it never closes remote handles or deletes/reopens paths.
Observed source shrink/overflow is irreversible even if its length is restored.
Copied wrappers cannot use or dispose the original. Native retention can survive
an app's abrupt exit, but that is NOT successful attachment/exit/drain/EOF evidence.

#### Remaining first integration edge

The next step is the separately versioned, original-request-owned rendezvous:
register it from the original A8b10 GUI owner, make it ready only for the sealed
two-app set, bind the original Core lease/catalog or explicit writer-only owner,
publish one immutable manifest with its actual handles still retained, and issue
one exact acknowledgment only after all-or-none pull adoption and original lease
rechecks. Neither a caller-created manifest nor the primitive's callback may stand
in for those reference/native ownership links. Wire state/nonce, submission
immutability and acknowledgment races must be reviewed and tested before selecting
the source-retaining writer path. Final sealing, original finalizer gates and
primary/parent publication remain later work. Existing app/GUI wire formats and
the isolated frozen-source path are unchanged.

See `artifacts/frontier-file-handoff-20260916/RESULTS.md`. Transport tests use actual
Core native payload/source files and real child processes with synthetic receipt
metadata; they do not claim original producer/writer custody or live acceptance.

### A8b13: original-request file rendezvous (implemented; writer submission remains gated)

2026-09-16. The original GUI launch owner now exposes a one-shot
`TryEnableFileHandoff()` with no imported scope, endpoint, process, manifest or
callback. It requires its original sealed two-request/two-app registry and live
bound processes. It creates the two separate endpoints and only then signals
readiness. Plain launch registries, unsealed/incomplete sets, repeated enabling,
closed/completed app requests and original GUI/parent revocation fail closed.
Nothing in the production GUI calls this method yet.

The private Core catalog can make one `TryAttachFileHandoff()` attempt through its
exact original native app lease and catalog reference. Native endpoint work is
outside service/catalog/logging gates. The app opens only the endpoint derived
from that original lease's name, compares native GUI/app process incarnations,
and retains its existing original native GUI process handle and request checks.
No endpoint argument or environment flag can choose another channel. A missing
or not-ready endpoint cannot be retried after it later appears. No-channel app
requests retain their existing version-1 attachment/completion behavior.

The lease now rejects copied wrappers for currentness, completion and disposal.
The file handoff itself is also original-reference-owned. Copied app leases,
file handoffs and GUI request wrappers cannot submit, adopt, acknowledge or dispose
the original. Catalog attachment and handoff retirement also require the exact
original catalog reference; a copied catalog cannot import or retire that channel.
This is trusted local workflow ownership, not protection
against arbitrary same-process reflection or hostile same-account memory access.

#### Separate handshake header and signals

Each endpoint is `<original-app-request-name>.files-v1`, with a fixed 192-byte
header and a 32768-byte manifest area. Both endpoints together retain A8b10's
65536-byte descriptor cap. Existing app and GUI request/completion bytes are
unchanged. There is no path-based exchange or remote handle push.

| Offset | Field |
| --- | --- |
| 0 | Int32 magic `0x4e464a31` |
| 4 | Int32 version `1` |
| 8 | Int32 state, 0 through 5 |
| 12 | Int32 original GUI slot, 0 or 1 |
| 16 | Original GUI request GUID |
| 32 | Original app request GUID |
| 48 | Fresh GUI-issued channel GUID |
| 64 / 68 | Int32 GUI PID / app PID |
| 72 / 80 | Int64 GUI creation FILETIME / app creation FILETIME |
| 88 | Int32 manifest length |
| 92 | Int32 reserved zero |
| 96 | SHA-256 of the exact encoded manifest, 32 bytes |
| 128 | Fresh app-issued submission GUID |
| 144 | GUI acknowledgment of that exact submission GUID |
| 160..191 | Reserved zero bytes |

The manifest digest is a control-channel integrity check over A8b12's encoded
manifest, not a producer-payload or final-source hash. Scope fields and reserved
bytes are rechecked throughout. Each side retains its own exact submission bytes,
digest and nonce when available. Observed state regression, changed metadata,
changed body/digest, wrong acknowledgment nonce or invalid size rejects the
channel. Claimed lengths cannot allocate beyond the fixed local cap.

The named `.ready`, `.submitted` and `.adopted` events are notifications only;
none grants custody without the matching header, original ownership and byte
checks. `.cancelled` irreversibly revokes the optional channel. State transitions
use the separate `.gate` mutex with zero-time acquisition; contention/abandonment
omits diagnostics, never retries or waits for runtime work.

| State | Sole normal writer | Required transition |
| --- | --- | --- |
| 0 Ready | Original GUI | Both original endpoints registered; sealed launch set current |
| 1 Attached | Original Core app lease/catalog | One exact attachment under the channel gate |
| 2 Submitted | Original app handoff | Complete immutable manifest, digest and new submission nonce; all advertised handles retained |
| 3 Adopting | Original GUI | Capture and verify one complete submitted descriptor before file work |
| 4 Adopted | Original GUI | All-or-none native pull succeeds; original app attachment is still open and process alive; descriptor unchanged |
| 5 Acknowledged | Original app handoff | One exact acknowledgment take while the original lease/catalog is still current |

`TrySubmit` is internal and currently test-called only. It snapshots the manifest,
checks its original GUI/app/process scope, and pins the exact supplied original
`SafeFileHandle` objects in descriptor order. Pins prevent native handle closure
or reuse while GUI pull is pending; they do not open additional native files.
No successful prefix is published. The app releases pins only after its exact
acknowledgment take or cancellation/failure. Every failed/repeated submit or
acknowledgment attempt retires the optional channel. No synchronous acknowledgment
wait or polling loop is added to Core.

GUI `FrontierAppRequest.TryAdoptFiles()` accepts only its original wrapper. It
captures the descriptor under the channel mutex, releases that mutex for bounded
file duplication/hash work, then reacquires it and rechecks original GUI/app
ownership, still-open attachment, live app and exact descriptor before publishing
the nonce acknowledgment. The app cannot complete its attachment and then use its
retired catalog to acknowledge. A signaled event alone, copied nonce, late
successful exit or replay cannot recreate eligibility.

An attachment-completion attempt before acknowledgment cancels the optional file
handoff; it does not wait for acknowledgment or add a file prerequisite to the
legacy attachment-completion operation. That operation still uses its original
native request/cancellation gates. Successful acknowledgment followed by successful
original attachment completion preserves GUI-owned files through ordinary app
catalog cleanup. A failed completion, premature catalog cancellation, nonzero exit
or app/parent death rejects the complete optional output.

`AllFileHandoffsAcknowledged` is separate from
`AllAppRequestsCompletedSuccessfully`: acknowledgment alone proves neither native
exit success, writer drain, output EOF nor final source integrity. The GUI checks
both original contributions and all owned file/descriptor state; any failure
retires the bounded adoption set. It never closes app handles or terminates apps,
and the old GUI finalizer attach slot remains independent.

#### Work accounting and cancellation

Descriptor capture and acknowledgment work wrap A8b12's native pull in its same
cumulative five-second stopwatch. Nested adoption does not stop/reset the outer
measurement; post-control checks can still reject and release the entire set.
The later final source verifier must keep this budget. There is no additional
five-second allowance or new runtime deadline.

Adoption callbacks inspect the immutable sealed GUI registry without taking its
outer lock, avoiding inversion with GUI disposal. Catalog invalidation signals
file cancellation first and uses a nonblocking local-lock attempt for pin release;
an active submission/acknowledgment releases pins in its own cleanup. A focused
concurrency fixture holds the catalog gate while an acknowledgment holds its own
gate, then cancels the catalog: cancellation returns and the acknowledgment
rejects without deadlock. File creation, duplication, content reads and hashing
stay outside service, catalog, logging and shared-memory channel gates. Owned
handle cleanup remains synchronous; no hard native-call latency guarantee is made.

#### Remaining genuine-writer edge

The request protocol and original Core catalog identity are now connected, but
there is still NO production submission issuer. Transport fixtures deliberately
use synthetic range metadata with real native files, including the writer-only
wire variant; they do not establish an actual writer-only registration, exact
persisted completion/marker/drain receipts, or original prepared-payload authority.
The internal submit API, scalar manifest, or acknowledgment cannot select the
source-retaining writer path by themselves.

Next, add the private catalog/writer-owned provisional submission issuer, including
an explicit original writer-only owner. It must retain exact receipts and actual
payload/source handles, use this original channel and preserve existing output
bounds without per-app shared-source freezing. Then implement GUI final sealing
only after every original drain, successful attachment/exit and original output
EOF gate. Runtime capture, finalizer output, primary/parent custody and correctness
approval remain disabled/later. No live-256 test, portable rebuild, commit or push
is implied by this handshake checkpoint.

See `artifacts/frontier-file-rendezvous-20260916/RESULTS.md` for focused real-process
protocol, cancellation/race and affected compatibility verification.

### A8b14: genuine catalog/writer provisional submission (implemented; production disabled)

2026-09-16. The private Core catalog now has a no-manifest-input issuer. Original
file-handoff attachment must precede writer binding and producer admission. The
writer registration retains that exact handoff and checks its original native
lease/catalog/channel scope without calling into the catalog under the logging
gate. A failed handoff attachment cannot fall back to the isolated frozen-source
writer mode. Standalone, never-attached catalogs keep their previous behavior.

#### Actual source capture and receipt ownership

The handoff-bound writer's original drain retains its actual append handle through
`OperationalLogNativeAnchor`, with read-only access and read/write/delete sharing.
This is captured in the existing append critical section; it is not a later path
reopen, whole-source read, hash, or per-app freeze. The pump owns the original
receipt and anchor, including immediate/deferred append and abandonment cleanup.
Acquisition consumes the original drain once and requires 1–33 distinct original
persisted receipts, same native file identity, fitting nonoverlapping ranges, and
the unchanged 256 MiB source bound. Copied/foreign receipt sets fail. Other writers
can still append and rotation can still rename/delete the retained source.

The catalog requires every admitted prepared payload, its exact completion and
marker, and the original successfully flushed drain. It builds immutable wire
metadata only from these original objects. Missing, misordered, extra, failed or
revoked receipts cannot issue a manifest. Native retention does not verify source
content: same-length mutation remains a later GUI-seal check.

#### Explicit writer-only ownership and all-or-none output

`TryTakeWriterOnly` issues a separate original-reference contribution only for a
handoff-bound catalog with no admissions. It prevents later producer admission,
cannot be substituted for an empty positive `PreparedCatalog`, and at submission
requires zero completion/marker receipts plus the real original drain. A copied
contribution cannot consume the original. Copied catalog/registration cleanup
cannot retire the original catalog's files, writer receipts, or channel.

For positive output, the issuer writes and verifies the original prepared payload
bytes as bounded native temporary files, then encodes one manifest and submits
the actual retained handle objects to the original handoff. No caller supplies
writer IDs, spans, observation IDs, payload hashes, filenames, endpoints or handles.
Payload creation/copy/hash and manifest encoding run outside service, catalog,
logging and channel gates. The existing per-catalog five-second output budget,
16-payload / 4 MiB bound and fixed manifest limits remain unchanged. No extra app
read-handle set is created: submission pins the original source/payload handles.

No prefix is submitted when any payload or receipt fails. Original repeated
submission, catalog cancellation, and post-drain writes retire the optional
channel and files. A writer revocation also cancels an already-submitted handoff;
successful acknowledgment plus successful attachment completion preserves normal
GUI custody during app cleanup. An actual new write attempt is not that cleanup:
it cancels even after acknowledgment and successful attachment completion, before
the original app endpoint is disposed. This does not extend endpoint lifetime or
replace the later shutdown/output-EOF gates. The old no-channel writer/app/GUI behavior and
runtime/coordinator authority are unchanged.

#### Verification scope and remaining gate

Native two-app fixtures now use actual accepted producer preparation, actual
completion/marker/drain writes, and this no-input issuer. The first app drains
before the second starts its writes to the same source. Both PowerShell hosts
exercise positive-plus-writer-only and two-positive-app handoffs, exact payload
and raw-range hashes, copied owners, missing/early receipts, second-payload
corruption, cancellation, repeat submission and late writes. Low-level pump tests
cover deferred batches, native rotation, bounds, revocation and abandoned drains.
See `artifacts/frontier-genuine-submission-20260916/RESULTS.md`.

The positive native fixture explicitly cancels the lifetime of each manually
constructed transfer context before draining its process writer. Without that
test cleanup, delayed grace diagnostics attempted writes after drain and correctly
revoked the contribution. This is not a runtime retry/callback-policy change or
permission to ignore post-drain writes during eventual production integration.

Production capture is still disabled. The next first edge is GUI final sealing:
retain both original contributions, require both successful app attachments and
native exits plus original stdout/stderr EOF/drain evidence, then seal the actual
shared-source handle and verify every original raw range using the existing GUI
work budget. This checkpoint does not provide final source integrity, finalizer
output, primary/parent custody, live/full-writer publication eligibility or an
acceptance verdict. No live-256 run, portable rebuild, commit or push occurred.

### A8b15: original app-output EOF ownership (implemented; production disabled)

Review of A8b14's next gate found an unimplemented prerequisite: the early GUI
registry retained original native process handles, but not original stdout/stderr
capture completion. The existing production event jobs and quiet-file polling
cannot be imported as a new EOF capability. They remain unchanged.

An original `FrontierAppRequest.StartAppWithOutput` now accepts one **unstarted**
`Process` with both streams redirected and shell execution disabled. The original
GUI request starts that process, acquires both original raw streams before its
first reads, and binds the same actual process handle/incarnation through the
existing native request registry. No caller reader, task, EOF boolean, output
path or process-ID lookup can supply output evidence. Already-associated process
wrappers, repeated/post-seal starts and missing redirects fail closed; copied
request/launch/owner wrappers cannot start, complete or revoke the original set.
The old `BindApp` API does not acquire output evidence retroactively.

Each registered launch owns two asynchronous raw-stream pumps and two create-new
capture files under the original registered artifact root. Each pump uses one
32-KiB buffer, without line accumulation, decoding or `ReadToEnd` allocation.
There are at most four pumps/capture sinks for the two-app registry; these are
process-output resources, separate from the provisional payload/source handles.
Capture files are diagnostic logs, not publication capabilities or sealed inputs.
Their contents have no post-close integrity claim, and their total size/lifetime
is governed by the run's existing outer process/output deadline, not the payload
byte caps or a reset of the five-second adoption/verification work budget.

`OutputDrainedSuccessfully` requires both original pumps to observe real EOF,
successfully flush and close their own capture sinks, and finish without a read,
write, flush or close failure. `AllAppOutputsDrainedSuccessfully` additionally
requires the original sealed two-app registry and current original ownership.
Neither property proves native exit, successful app attachment, operational
writer drain, file handoff or source integrity. In particular, closed streams
before process exit and process exit before inherited-stream EOF remain distinct.

Optional-lane revocation permanently removes EOF eligibility but does not kill
apps, close their live pipes or stop capture. A failed sink likewise makes the
whole optional EOF gate fail while its pump keeps draining/discarding that stream
until EOF, so diagnostic failure does not introduce pipe backpressure. Pumps
close their own sinks when they finish; the original caller still owns process
cleanup and must not dispose its process/streams before capture is drained. This
is cooperative capture under the outer deadline, not a forced-I/O timeout or a
promise of successful startup/cleanup after arbitrary external stream disposal.

Native tests on both PowerShell hosts cover exact binary/empty output, missing
capture, invalid/repeated starts, startup/sink-open rollback, copied owners, failed capture, cancellation,
nonzero exit, EOF before exit, and a still-inherited stdout or stderr pipe after
successful original app exit. Capture and original native exit evidence also
survive disposal of the original `Process` wrappers after successful drain.
See `artifacts/frontier-output-ownership-20260916/RESULTS.md`.

Production `Start-AppInstance`, its output event jobs, and its finalizer have not
been rewired. The first remaining implementation edge is final source sealing
that joins both original file contributions/drains, successful attachments/native
exits, and this original output evidence. It must use the retained native source,
all original raw ranges, and the same cumulative GUI work budget. No final seal,
finalizer/primary/parent output, live eligibility, correctness approval, runtime
authority change or new live-256 run is established by this checkpoint.

### A8b16: original GUI final source sealing (implemented; production disabled)

The original GUI launch owner now exposes one no-input `TrySealFiles` attempt.
It accepts no descriptor, source path, replacement reader, caller hash, EOF
boolean or closure callback. Its own sealed two-app registry must retain both
original acknowledged file contributions, successful original attachment
completion/native exits, and both original output pumps' successful EOF/capture
completion. Each original manifest still accounts for its writer's exact drain,
including explicit writer-only contributions. Missing/early gates fail the whole
optional lane; a later successful exit/EOF cannot revive a failed attempt.

The internal adoption primitive remains transport rather than independent writer
authority. Its closure callback is supplied only by the original GUI wrapper;
synthetic tests of that primitive cannot manufacture a GUI seal capability. The
callback uses the immutable sealed registry without acquiring the GUI lock, so
verification under the adopter lock cannot invert GUI disposal's lock order.
Whole-source reads/hash work run outside GUI/service/catalog/logging locks.

After all gates pass, the GUI reopens the actual retained source handle with
read-only access and read sharing only. No source filename is searched or reopened.
An existing ordinary write/delete handle prevents sealing; the successful seal
excludes new ordinary write/delete opens. Both original native source identities,
captured minimum lengths and the 256-MiB final source cap must match. A pathname
rotation/replacement does not redirect custody: the retained original native file
is sealed, not whatever now occupies the former pathname.

One sequential traversal uses a 32-KiB buffer and bounded SHA-256 state for the
whole file and all original raw ranges (2–66 ranges across the two writers).
Ranges must fit and not overlap; completion, marker and drain raw hashes all
must match. The whole-file digest is computed at final sealing, not imported from
either app's provisional snapshot. Bytes outside the original receipt ranges
contribute to the final whole-file hash but have no earlier whole-file hash to
compare against. This step does not classify source lines, establish unknown
writer absence, or perform another authority evaluation.

Closure/currentness and the existing cumulative five-second GUI work budget are
rechecked before/after reads and before the result is retained. Adoption, control
and final verification do not get independent/reset budgets. Idle process/output
waiting remains excluded. There is one additional sealed-source handle, used for
the traversal itself; no extra read handle or per-range reader is created. All
previous payload/source handles remain within the existing 34-handle bound.

A changed receipt range, truncated/oversized source, conflicting open writer,
failed native operation, cancellation, missing gate, budget expiry or repeat
attempt closes the entire optional adopted/sealed set. A successful
`FrontierSealedFiles` wrapper is reference-owned by the original GUI and remains
revocable. Its byte count, digest and range count are diagnostic scalars, not an
import API. Copies cannot validate or dispose the original. It exposes no source
reader, handle export or publication method at this checkpoint.

Verification includes synthetic native file tests of boundary-spanning ranges,
zero/one/16 payloads per app (up to all 66 ranges), retained-native rotation,
mutation, cumulative budget and cleanup. Separate original parent/GUI/two-Core-app
fixtures use genuine producer/writer submissions, original output capture, and
the new no-input GUI seal. Production launch/capture and the legacy finalizer
remain unchanged; all live/full-custody/publication flags remain false. Results
are recorded in `artifacts/frontier-final-source-seal-20260916/RESULTS.md`.

Next: a distinct bounded GUI finalizer/output handoff that consumes this original
sealed set and preserves the old completion schema, then original primary/parent
custody. Final source verification alone does not supply those capabilities or
authorize production capture, correctness reclassification or a live-256 run.

### A8b17: original finalizer ownership binding (implemented; production disabled)

Corrected source distinction after the A8b18 code review: in append-only mode,
the GUI configures the same acceptance-log pathname for both apps and the ledger
processor's input. A8b16 retains/seals the original native file; the legacy
processor independently reopens that pathname. Matching path strings do not bind
those readers to the same native file after rotation/replacement. The generated
ledger/writer/semantic slices and analyzer copy are separate output files and
their identities/hashes must not be substituted for the raw source. Attaching the
original finalizer is not evidence that later validation/copy/persistence passed.

The original `Finalizer.TryTakeFrontierBinding()` therefore performs only one
no-input in-process ownership binding. It obtains the existing exact sealed-files
wrapper from the original launch owner. No path, descriptor, reader, hash, copied
token, success boolean or closure callback is accepted from the caller. Both
owners must refer to the exact original native endpoint, not merely matching
request/run/scenario/root metadata: distinct endpoints can share all that metadata
and the same parent/GUI process incarnations. Relay finalizers are ineligible.

The original finalizer must remain uncompleted and undisposed, with native stage
1, zero completion length/reserved bytes, matching parent/GUI incarnations and a
current original sealed set. Retained native-file checks continue charging the
existing cumulative GUI work budget. No hashing pass, handle duplication, file
reader, authority evaluation, shutdown wait or new budget is added.

The exact-reference binding rechecks both owners and the sealed set. A failed
original attempt, repeat, observed stale scope or disposal permanently retires
the matching optional set. Restoring modified header bytes cannot revive it.
Copied finalizers/bindings cannot consume or retire the original. An unrelated
same-metadata endpoint cannot acquire or dispose the original sealed set.

Every original legacy `Complete` attempt retires the matching optional set before
legacy serialization, even if that completion later fails validation. Original
finalizer disposal does the same. Retiring a binding does not close the parent's
legacy request or prevent otherwise valid legacy completion. The native legacy
header, body schema, bounds and parent decoder remain unchanged. Lock order is
finalizer, launch owner, adopter; no owner/adopter callback acquires the finalizer
lock. Production capture/finalizer wiring remains disabled; all live proof,
full-custody and publication flags remain false.

The next edge is a separate bounded GUI-output contract: relate original raw
source origins to the finalizer's independently validated outputs, then define
original-parent registration, pull/adoption, acknowledgment and cleanup before
GUI exit/legacy completion. This binding does not claim any of those gates or
authorize a live-256 run. Verification is recorded in
`artifacts/frontier-finalizer-binding-20260916/RESULTS.md`.

### A8b18: original sealed-source reader (implemented; production disabled)

The code review traced the append-only path through `GuiSmoke-Windows.ps1`:
`Clear-AppLogsIfPresent` configures `live-operational-log/nlink.log`, the per-app launch
environment uses `GuiSmokeAppLogLedger.AppendOnlyPath`, and the finalizer passes
that same property to `GuiSmokeLogLedgerStreamingProcessor`. Its current input
constructor reopens by pathname with read/write/delete sharing. Therefore the
first missing reader edge is native ownership, not a join between two presumed
independent raw/ledger input files. The preceding A8b17 wording is corrected above.

An original current `FrontierFinalizerBinding` now has one no-input
`TryOpenSource()` attempt. It reuses the adopter's existing sealed `FileStream`
and handle, resetting that original stream from completed seal traversal to byte
zero exactly once. It accepts no pathname, descriptor, handle, caller stream,
digest, source selector or success boolean. No new native handle is opened or
duplicated, and no payload file or private producer object is exposed.

The returned exact-reference `FrontierSourceRead` is read-only and non-seekable.
Each positive read returns at most 32 KiB and checks native scope, file identity,
length and the existing cumulative five-second GUI budget before/after I/O.
Reads stop at the sealed source length; a short/failed read, unexpected position,
stale scope or expired budget retires the optional set. The original handle's
write/delete exclusion remains in effect. Path rotation/replacement before seal
cannot redirect reads. There is no second hash pass or reset/per-read work budget.
Only active read/control checks are timed here; this does not attest to a later
parser's CPU work. Finalizer/GUI locks are not held across the read operation.

Copies cannot read, consume the original open attempt or dispose the original.
Repeated opening, original reader/binding/finalizer disposal and original legacy
completion attempts revoke access; restoration of stale state cannot revive it.
Disposing a reader also releases the whole optional sealed/payload set. Invalid
stream arguments and unsupported seek/write APIs do not grant access or authority.

`ReachedEnd` is diagnostic evidence of reading through physical EOF. A zero-length
read cannot establish it. Reader buffering or copying all bytes is not proof that
the finalizer parsed/validated any line, created a native output, persisted a
semantic copy, or published a result. Live/full-custody/publication flags remain
false. The existing path-based processor and production finalizer are unchanged.

Next, integrate this original reader with the existing streaming processor in a
distinct opt-in path. Bind the original processing result and exact physical-line
origins to its outputs without replaying authority evaluation, replacing the old
completion schema, or treating `ReachedEnd` as validation. Then define the bounded
original-parent output pull/adoption/acknowledgment/cleanup channel. Verification:
`artifacts/frontier-finalizer-source-20260916/RESULTS.md`. No live-256 run is enabled.

### A8b19: owned-source processor and physical origins (implemented; production disabled)

`Invoke-FileTransferFrontierLedgerProcessing` is an explicit opt-in helper, not a
production finalizer call site. It accepts the original finalizer binding and the
existing frozen-line/bookmark and event/deferred-batch selections. It derives
run/scenario/root from the original binding, opens its source exactly once, and
creates three uniquely named temporary outputs under that original artifact root.
No source path, replacement reader, native descriptor or imported success verdict
enters this GUI helper. The actual parent/GUI/two-app fixture exercises it with
genuine writer submissions and a retained source whose old pathname can be replaced.

The processor's new `ProcessFromStream` entry reuses the existing parsing, writer
checks, chronology folding, event selection, output bytes and hashing loop. This
lower-level API accepts a generic read-only stream and supplies stream provenance
only; a synthetic stream cannot establish original GUI custody. The original GUI
helper independently supplies its own `FrontierSourceRead`. Legacy path-based
`Process` / `ProcessWithNativeCapture` and the production finalizer are unchanged.
The new path does not populate the old `NativeCapture` or legacy completion record.

The source must start at byte zero, have 1 byte–256 MiB of captured data, and contain
exactly the declared 1–262144 physical lines. An extra line or early EOF omits the
optional result; consuming a frozen prefix alone is insufficient. Strict decoding
and CR/LF/CRLF line handling preserve existing text/output behavior. A 32-KiB
character buffer and a 262144-UTF-16-code-unit per-line cap bound line allocation
before a complete line is materialized. Existing input read bounds remain 32 KiB.

Each emitted retained/writer/semantic line gets its exact one-based physical source
line ordinal in the same processing loop. Origin entries are capped at 262144 in
aggregate across all three outputs (1 MiB of logical 32-bit ordinal data; list
capacity remains bounded). No entry is sampled, evicted or inferred from a nearby
success. This is a separately bounded in-process origin map, not an expansion of
the existing 64-KiB file-handoff manifest or the later 64-witness selection budget.
Deferred keys remain capped at 64; semantic selectors are capped at 2048 as of
A8c34 (originally 1024); each selector/key stays capped at 1024 code units and
their aggregate text at 65536 code units.

The original reader exposes read-only length/position observations plus a one-shot
processing-work interval. The original adopter's existing stopwatch keeps running
across decoding, validation, origin recording, hashing, flushing and capture; nested
reads cannot stop/reset it. The same cumulative five-second GUI budget applies,
including prior adoption/sealing. A standalone stream processor also has a five-
second ceiling; this is not an additional/reset allowance for the original GUI.
No finalizer/GUI/adopter lock is held around the whole processing loop. Repeat,
partial processing, expired budget, cancellation or disposal retires the optional
set. Work accounting is not a parser/finalizer success capability.

The original result privately owns a distinct `GuiSmokeLedgerStreamCapture`:
original stream identity, immutable run/scenario/byte/line/bookmark observations,
creation-time native output identities/hashes/counts, read-only origin lists and a
snapshot of processor writer-error presence. Copied result/capture objects cannot
validate ownership. Mutable result fields cannot alter this captured error snapshot.
`IsCurrent` / `MatchesOriginalSource` validate the original source relationship,
not the present contents of output paths. They do not attest to finalizer validation,
semantic-copy persistence, unknown-writer checks beyond the existing processor,
or output-file immutability. All live/full-custody/publication flags remain false.

On failure, no processing record is returned and original source/payload custody
is retired. Created temporary outputs may remain as failed-run artifacts; they
are never published or substituted for the existing finalizer's output paths.
Successful provenance borrows the source until the original binding is retired.

Next: retain and seal the actual newly written output files, without reopening
them by diagnostic pathname, and bind the original finalizer's remaining validation,
copy and persistence gates. Then add bounded original-parent pull/adoption/acknowledgment
and cleanup before legacy completion/GUI exit, followed by primary custody. No
authority evaluation, acceptance reclassification or production opt-in is added.
Verification: `artifacts/frontier-ledger-processing-20260916/RESULTS.md`.

### A8b20: original output-handle retention (implemented; production disabled)

The next missing edge after A8b19 was native output lifetime: creation-time
identity/hash/count metadata did not prevent output replacement or mutation after
the processor's write handles closed. The explicit GUI helper now calls the new
`ProcessFromStreamWithRetainedOutputs` entry. Legacy path APIs and the standalone
metadata-only `ProcessFromStream` entry retain their prior output behavior.

Only this new entry creates each of the three unique outputs with read/write
access and read sharing only. After hashing, flushing and capturing metadata on
the original write handles, it duplicates those same native file objects with
read-only access. It neither reopens a pathname nor imports an external handle.
Identity and byte length must match the original creation captures. The writable
handles close before the result is returned; the three retained handles are
read-only, and the original file objects' sharing exclusion continues blocking
ordinary write, delete and rename opens. There is no unprotected close/reopen gap.
Test reads by pathname are diagnostics only and share the original file object's
write access; no production validation relies on such a path read.

There are exactly three additional retained handles. During sealing, at most six
output handles exist (three write-phase originals plus three read-only duplicates),
then only the three duplicates remain. The existing 34 provisional input handles
and one sealed-source handle are unchanged. Output creation/sealing remains inside
the original cumulative five-second GUI processing interval, with no second hash
pass or reset allowance. The A8b19 line, origin and metadata bounds still apply.

The exact original stream capture privately owns the retained set. Its
`HasRetainedOutputs` and `IsCurrent` checks require the original result/source and
all three original read-only native files with matching identities and lengths.
A missing/closed output retires all three outputs and the optional original source
when observed. Copied capture/lease objects cannot dispose the originals. No native
handle, output reader, output adoption token or completion issuer is exported.

The original source exposes a cleanup-only cancellation token during its active
processing interval. Copies, foreign reader tokens and inactive intervals receive
an already-cancelled token. The helper passes the original token; an arbitrary
token or generic stream cannot establish GUI custody. Original source/adopter
retirement synchronously closes the retained outputs. Cancellation of an external
run is observed by the existing ownership/currentness checks, which then retire
that original set; no new background watcher is added. The output cancellation
callback only closes files, without entering finalizer/source ownership locks or
unregistering itself. Source checks occur outside the output-set lock. Callback
exceptions cannot prevent the adopter from releasing its source/payload handles.

Failure before creation, during processing, or after native sealing returns no
retained result and releases all handles acquired by that attempt. Existing paths
are never overwritten. Failed temporary files can remain as unpublished artifacts.
Disposing the original capture retires its optional source as well as its outputs.

This is output custody, not finalizer approval. Writer-error snapshots, generic
stream provenance and retained files do not establish the remaining original
finalizer validation, semantic-copy or persistence gates. All live-proof,
full-native-writer-custody and publication flags stay false; the legacy completion
record is untouched. Next is binding those finalizer gates to the exact retained
result, then bounded original-parent pull/adoption/acknowledgment/cleanup and
primary custody. Verification: `artifacts/frontier-retained-outputs-20260916/RESULTS.md`.

### A8b21: immutable processor validation facts (implemented; production disabled)

Review of the remaining finalizer gates found that A8b20's native output retention
did not freeze the public processor result's mutable writer/drain/batch objects.
The stream capture now privately owns an exact-reference
`GuiSmokeLedgerValidationSnapshot` created before the result returns. It captures
original source-visit and processor-error counters, writer scope/sequence facts,
drain count/single-drain sequence and deferred-batch tuple/hash facts. Collection
entries are new immutable objects, not the original mutable result entries.

The snapshot preserves the processor-error distinction and separately checks
reported writer closure: at least one writer, correct scope, first sequence one,
positive final sequence, observed count equal to final sequence, and exactly one
drain at that final sequence. Missing, duplicate or non-terminal drains remain
errors even when the existing processor-error flag is false. This is a check of
reported source facts, not proof that the reported set equals the original native
writer registry. Deferred-batch facts are preserved, not approved as recovered
without the separate original process-marker evidence.

Bounds are 64 writers, 64 deferred batches, 1024 code units per captured text field
and 65536 aggregate captured text code units. The aggregate limit is inclusive.
No per-writer drain array is retained: its count and unique sequence suffice for
the existing drain rule. There is no new native handle, source traversal, hash
pass, independent work budget or authority evaluation. Construction remains in
the original processing interval, including a post-capture budget check.

The original capture must own the exact snapshot, and snapshot currentness checks
the original capture/source and retained outputs when present. Copied captures,
copied snapshots and snapshots from another invocation cannot satisfy ownership.
Changing the old result counters, replacing its arrays or mutating nested records
cannot alter the snapshot. Its facts remain diagnostic after retirement; consumers
must check exact ownership/currentness, not scalar equality.

Bounds failure omits the snapshot. Generic processor entries may still return
their ordinary diagnostic result/capture; the explicit GUI helper requires its
exact current snapshot and otherwise retires its optional source/output set. An
immutable snapshot with data errors is not relabeled as approval. All live-proof,
full-custody and publication flags stay false. Legacy path output/validation and
completion format remain unchanged.

Next: join these exact writer/drain facts to the two original adopted native
contributions, then establish original recovery-marker custody and bind cursor,
semantic-copy and persistence gates before the separate parent handoff. The gate
inventory and bounded sequence are in
`docs/file-transfer-frontier-finalizer-gates-rca.md`. Verification:
`artifacts/frontier-validation-snapshot-20260916/RESULTS.md`.

### A8b22: exact original native writer match (implemented; production disabled)

A8b21 captured immutable reported writer/drain facts, but did not prove that those
writers were the two original native contributions adopted from the actual app
requests. The original finalizer binding now accepts its exact typed streaming
result and privately joins that result's original capture/snapshot to the saved
original source reader and both adopted manifests. Public writer arrays, JSON,
copies, foreign source captures and success flags cannot supply this ownership.

The snapshot must have no processor or writer-closure errors, exactly two writers,
the original run/scenario, and both original retained output and source lifetimes.
Writer IDs match ordinally and order-independently; each final sequence must equal
that original contribution's native drain `Last` sequence. Missing/additional,
duplicate, substituted, case-changed and sequence-mismatched writers fail closed.
This is a terminal-sequence comparison, not a new single-line drain requirement.

The join is one-shot and occurs after complete original source processing but
before `TryFinishProcessingWork`, inside the existing cumulative five-second work
budget. An original failed/repeated/late attempt retires the entire optional
source/output/payload set. Copied bindings/results/matches and foreign results
cannot consume or retire the original attempt. An original match remains current
after successful processing finish only while its original binding, source,
capture, snapshot and retained outputs remain current. Cancellation, legacy
completion, closed output or original disposal revokes it; reset cannot revive it.
An original over-bound result with no validation snapshot also retires immediately;
absence is not classified as a harmless foreign candidate.

For compile-time ownership, the existing processor source is compiled into
`NLink.Tools.FrontierLedger` inside the optional ownership assembly using
`NLINK_FRONTIER_LEDGER`. The default standalone `NLink.Tools` processor remains
unchanged and can load before or after the ownership assembly. The shared loader
uses host-specific compiler options for PowerShell 5.1/7; there is no reflection
join, runtime source rewrite or extra processing invocation. The explicit helper
returns the original optional typed result only after the exact match and work
finish both succeed. No production caller uses that helper yet.

Only two locally derived writer IDs and sequences cross the internal transport
comparison. There is no public scalar factory, native handle export, second
source/hash pass, new handle, independent budget or authority evaluation. The
match is not recovery validation, cursor validation, semantic copy, persistence,
parent custody or full finalizer approval. Live-proof/full-custody/publication
flags remain false and the legacy completion format is untouched.

Next: original recovery-marker custody, then original cursor/copy/persistence
gates and bounded parent handoff. Verification:
`artifacts/frontier-writer-match-20260916/RESULTS.md`.

### A8b23: original recovery-output inputs (implemented; production disabled)

The A8b15 pumps owned each process pipe's first reads and EOF, but closed their
capture files before later recovery-marker analysis. A diagnostic-path reopen
could therefore substitute bytes. Each optional pump now creates its file with
read/write access and read-only sharing, flushes its own exact bytes at EOF, and
duplicates that same native file object with reduced read-only access before
closing the writable handle. There is no pathname reopen, hash reconstruction,
marker parser, additional output copy or interpretation of raw bytes in this edge.

All four original streams (two original apps, stdout and stderr) remain required,
including empty streams. Each retained stream is capped at 64 MiB, hence 256 MiB
for the fixed four-stream registry. Above this limit the optional retention/EOF
gate fails; ordinary diagnostic pumping still drains until EOF under the existing
outer process/output deadline. Capture is not truncated and apps/pipes are not
stopped on failure or revocation. This supersedes A8b15's post-close diagnostic-only
file lifetime without changing the production event-job launcher.

The output subsystem retains four read-only handles after EOF, with at most eight
file handles during simultaneous write-to-read handover, in addition to existing
source/payload and generated-ledger handles. Each existing pump still has one
32-KiB buffer. Revocation closes any retained readers immediately, prevents late
EOF from retaining a new one, and leaves live write-pump cleanup to those pumps.
No native handle or creation path is exported as authority.

After the original source seal, `FrontierFinalizerBinding.TryOpenRecoveryOutputs`
can issue one read-only collection of four original `FrontierRecoveryRead`
objects. Each binds the exact original finalizer, source/file-adoption owner,
native app incarnation, stream role and actual retained capture file. The caller
cannot import a path, stream, descriptor, process ID or EOF Boolean. Copied
bindings/readers cannot obtain bytes or retire originals. A repeated original
open, stale/closed file, changed private cursor, expired budget, original reader
disposal, cancellation, finalizer completion or owner/source retirement closes
the optional set; reset cannot revive it.

Readers begin at byte zero, cannot seek or write, and return at most 32 KiB per
call through their exact retained handles. Zero-length reads do not assert EOF.
Positive-size reads verify exact length/position and physical EOF, including empty
inputs. Each read uses the existing cumulative five-second adoption/source work
budget with before/after checks; it never resets that budget or stops an already
running ledger-processing interval. No independent clock or second source scan is
introduced. This accounts for raw I/O only: the next parsing step must explicitly
include all decode/parse/validation work in the original processing interval.

Read-only file sharing remains continuously in force. Because the reduced handle
shares the original read/write file object, diagnostic readers must still open
with `FileShare.ReadWrite`; they do not gain write access. A8b15 test inspection
uses that compatible sharing mode rather than assuming all creation handles have
closed. Original write/delete attempts are blocked until owner retirement.

Raw EOF, bytes and process identity do not prove a recovered tuple, successful
ledger/cursor checks, semantic copy, persistence or parent custody. Live-proof,
full-native-writer-custody and publication flags remain false. Next: parse these
four exact readers, capture bounded original marker facts and deferred batch keys,
then join exact unique deferred/recovered tuples to the original immutable ledger
facts while preserving terminal-failure and duplicate-field rejection rules.
No production call site, legacy completion, primary evaluation count or verdict
is changed. Verification: `artifacts/frontier-recovery-inputs-20260916/RESULTS.md`.

### A8b24: original recovery markers and exact tuple join (implemented; production disabled)

The original finalizer now parses its four A8b23 readers before ledger processing,
while the original source remains at byte zero. It first starts the existing
cumulative five-second processing interval; original reads, strict UTF-8 decoding,
line framing, field parsing, pair validation, processing and immutable tuple
comparison all share that interval. No new clock, path reopen, source pass,
authority evaluation or caller-supplied marker verdict is introduced.

The strict decoder is explicitly flushed at physical EOF: Framework
`StreamReader` alone silently accepted an incomplete UTF-8 tail during fresh
PowerShell 5.1 tests. An optional UTF-8 BOM is accepted, but UTF-16/32
auto-detection is not. CR, LF and
CRLF frame physical source lines; an unterminated final line is included. All
four original streams must reach physical EOF. In addition to A8b23's 64-MiB
per-stream limit, parsing permits at most 262144 total lines, 262144 UTF-16 code
units per line, 128 marker records, 64 deferred and 64 recovered batches, 1024
code units per captured input text field and 65536 aggregate captured text code units.
Over-bound or malformed input omits the whole optional result, never a partial
selection. The immutable snapshot retains each marker's original stream index
and physical line number. These are diagnostic origins, not runtime authority.

Recognized marker events and structured field names remain case-insensitive.
Duplicate structured fields on marker lines, duplicate deferred/recovered batch
keys, and any terminal write-failure marker (including its minimal legacy form)
fail closed. Each deferred/recovered pair must match writer, acceptance/scenario
scope, generation, first/last sequence, line count, attempt ticks and payload
hash. Hash comparison is case-insensitive; writer and scope comparisons are
ordinal. Attempt count and failure code remain metadata, not tuple equality.
Each marker's writer must also belong to that exact original app incarnation's
adopted native contribution, rather than merely naming either known writer.

The opt-in processing helper derives its deferred-batch keys from this original
snapshot; its caller-supplied `DeferredBatchKeys` parameter is removed. After
A8b22's original writer match, a one-shot `TryMatchRecovery` consumes only that
same typed result and immutable validation snapshot. It requires the exact batch
set, valid tuples, exact sequence endpoints/minimum/maximum/count, attempt ticks
and canonical payload hash. Mutable public result records are not re-read as
authority. Foreign or copied result/binding candidates cannot consume an original
attempt. Failed, repeated or late original capture/join retires the optional set.
Copied marker/match objects cannot remain current or dispose originals.

The successful match remains dependent on the original finalizer, four retained
recovery inputs, writer match, sealed source and three retained processor outputs.
Finishing the work interval preserves a successful match; cancellation, completion,
closed handles or disposal invalidate it and release resources. Live-proof,
full-native-writer-custody and publication flags remain false. No production
caller uses the opt-in helper; the legacy processor/finalizer is unchanged.

Verification uses genuine native parent/GUI/two-app contributions plus explicitly
synthetic recovery-marker text derived from their genuine persisted drain tuples.
It does not claim the fixture's actual writer experienced retries, or establish
live NKN/GUI route correctness. See
`artifacts/frontier-recovery-markers-20260916/RESULTS.md`.
Next: original cursor observations, semantic-copy and persistence operations,
then bounded original-parent handoff and primary custody.

### A8b25: original ledger-file creation (implemented; production disabled)

The remaining cursor gate cannot be satisfied by copying mutable PowerShell
snapshots at finalization. Its first ownership prerequisite is the actual ledger
file creation. The optional original GUI launch owner now creates exactly one
`live-operational-log/nlink.log` under its registered artifact root before any app
request/launch. The operation takes no caller path, file, snapshot or success flag.
An existing file is never overwritten; a reparse-point log directory is rejected.

The native file is created with `CREATE_NEW` and read access only. Its one retained
creation handle permits app read/write access but excludes replacement/deletion.
No writable creation duplicate survives, so the final restrictive read-only source
seal can coexist with it. There is no handle export, path reopen, new source scan
or hash pass. Initial empty length is checked; the existing 256-MiB source limit
also bounds this retained input. Source/diagnostic files remain after retirement.

Creation starts the same cumulative five-second work clock later used by the
original adopter, source verification and processing. No reset or independent
allowance is granted, and idle app execution is not charged. If creation was
requested, final sealing compares the exact retained native file against both
adopted writer sources; identical content in a different native file is rejected.
Only the original finalizer binding can expose the exact sealed creation owner.

Copied owners/origins/bindings cannot acquire or retire originals. Repeated/late
creation, preexisting source, expired budget, closed handle, unexpected private
position, over-bound length, cancellation, finalizer completion and disposal fail
closed. Owned handles and dependent optional resources close, without stopping
app processes or deleting their logs. Source path and ledger ID remain locators.

This proves creation custody only. The scenario takes its initial bookmark after
helpee startup; `WasCreatedEmpty` is not that observation. `CursorHistoryValidated`,
live-proof, full-native-writer-custody and publication remain false. Existing paths
without an origin retain their prior disabled-helper/legacy behavior; no production
call site is added. The future cursor gate must require the original origin and
own actual bounded incremental indexing and initial/final observation operations.
Do not mint those observations from caller-provided scalar snapshots.

RCA and continuation: `docs/file-transfer-frontier-ledger-origin-rca.md`.
Verification: `artifacts/frontier-ledger-origin-20260916/RESULTS.md`.

### A8b26: original incremental cursor observations (implemented; production disabled)

The original ledger creation owner now indexes its retained native stream, with
no path reopen or imported bytes/snapshots/verdict. Each synchronization reads
only the unread prefix-to-captured-length suffix in 32-KiB blocks. LF completes
lines; CR and unterminated tails do not. Read and committed offsets are separate,
so partial bytes are not reread. The private index holds at most 262144 line-start
offsets, under the existing 256-MiB source limit and shared five-second work clock.

The one-shot initial observation requires exactly one bound live app, before the
second launch and launch sealing/handoff. It performs synchronization and freezes
the actual bookmark/count/byte positions, including a possible incomplete tail.
The eventual production call must remain at the existing post-helpee-startup
bookmark point; the phase guard itself does not prove app readiness. Creation at
zero is not substituted. Later synchronizations cannot mutate that observation.

After source sealing begins, public live synchronization fails closed. Only the
original finalizer binding can capture the final observation, before any source
reader bytes are consumed and within active original processing work. It requires
the original initial observation, consumes the remaining cursor suffix, requires
fully LF-committed EOF, and compares its incrementally computed SHA-256 with the
original sealed-source hash. Already-observed prefix rewrites are rejected even
outside native writer ranges. No cursor rescan, extra source hash traversal,
native handle, independent allowance, or authority evaluation is added.

The launch lock serializes pre-adopter indexing with app binding/handoff creation;
the adopter lock serializes later indexing with adoption/sealing. Final capture
preserves the existing running processing interval. Resource callbacks never
acquire GUI/finalizer locks. Exact original observations alone remain current;
copies cannot operate or dispose originals. Invalid phase, repeat, missing initial,
incomplete final line, hash mismatch, truncation, position change, closed input,
expiry, cancellation, completion and disposal retire the optional resources.

Only two immutable observations are retained. They are byte/index facts, not
semantic writer/generation snapshots or exact invocation agreement. The next gate
must join their exact bookmark/count and cursor diagnostics to original processor
facts, deriving prefix writer facts during the same pass. `CursorHistoryValidated`,
live-proof, full-native-writer-custody and publication remain false. The existing
disabled helper and legacy path are unchanged; no production caller is enabled.

RCA: `docs/file-transfer-frontier-ledger-origin-rca.md`.
Verification: `artifacts/frontier-ledger-cursor-20260916/RESULTS.md`.

### A8b27: exact owned cursor/processor join (implemented; production disabled)

The original binding's separate `TryProcessObservedLedger` operation accepts only
semantic-event selection. It supplies its actual observed bookmark/count, original
source reader, recovery-derived batch keys, run scope and new output destinations
under the registered root. It performs one processor invocation under the same
cumulative five-second interval, then requires native writer, recovery and cursor
joins before finishing work. No source/snapshot/path/hash/Boolean is imported as
authority; the previous scalar helper and production call sites are unchanged.

The internal owned-cursor reader frames LF bytes exactly as the original index,
preserving embedded CR as data and removing one terminal CR from CRLF. UTF-8 is
strict, including incomplete tails on Framework; only an initial UTF-8 BOM is
recognized. Every line must end in LF. Existing source/line/index bounds apply:
256 MiB source, 262144 lines/UTF-16 units per line, 32-KiB reads, and a per-line
raw-byte guard of four times the character cap plus three BOM bytes. Legacy path
and generic stream readers retain their prior behavior. No additional source
traversal, whole-source hash pass or authority evaluation occurs.

Physical byte starts are captured during that same pass and compared one-for-one
with the original private cursor index. The exact source bytes, final line count,
bookmark and immutable visit count must agree. Initial writer identity/scope and
sequence/count facts are frozen at the bookmark before its line is processed;
their at-most-64 records share the already-bounded immutable final writer strings.
They must be a monotonic scoped subset of the valid final/native writer set.
Mutable result arrays/counters cannot change this comparison.

The retained original source cannot rotate and defines a single active generation
from creation through seal. Its original generation ID is distinct from ledger ID;
both observations belong to that same original generation, with monotonic byte
and line counts. Gaps/duplicate indices, count disagreement and earlier cursor
failure prevent a match rather than importing zero counters or an empty reasons
array as a success assertion.

Only the exact current `FrontierCursorMatch` reports `CursorHistoryValidated`.
The original observation objects alone still report false. A match requires the
exact original result/source/capture, current immutable validation, writer and
recovery joins, source EOF and active work. Copied/foreign candidates are harmless;
failed/repeated/late originals revoke the optional set. Source/output/observation
retirement, cancellation and legacy completion revoke the match after capture.
All live-proof, full-native-custody and publication flags remain false. At A8b27,
semantic copy, evidence persistence and primary/parent custody remained absent;
A8b28 below implements only the original copy subgate.

RCA: `docs/file-transfer-frontier-ledger-origin-rca.md`.
Verification: `artifacts/frontier-cursor-match-20260916/RESULTS.md`.

### A8b28: original semantic-copy operation (implemented; production disabled)

`TryCopySemanticEvidence` accepts no caller inputs. The original binding requires
its current cursor/writer/recovery matches and completed processing, then copies
from the private retained semantic handle into a create-new destination derived
under the original artifact root. No path reopen, imported result/verdict or
second processor/source pass supplies authority. Existing files are not replaced.

I/O uses 32-KiB blocks under the same cumulative five-second adopter clock, with
currentness checks throughout. The copied byte count, SHA-256 and physical line
count must match the original immutable semantic capture and exact origin count.
The legacy LF-count/unterminated-last-line rule is preserved, including empty
output. Existing source/line bounds cap output including newline expansion.
After flush, the actual creation handle is reduced to one retained read-only
duplicate. Its original native identity/length and the original input set remain
required; source/output/destination retirement closes the optional set.

The separate `FrontierSemanticCopy` is reference-owned and revocable. Copies and
foreign metadata cannot claim or retire it. Missing prerequisites, repetition,
hash/count disagreement, collisions, closed handles, budget exhaustion and
cancellation fail closed. The original cancellation registration also closes the
copy; no new lifetime or time allowance is created. Callbacks do not enter GUI or
finalizer locks from retained-output I/O.

This gate owns creation and the copy operation, not atomic path publication or
evidence persistence. A failed partial destination remains diagnostic-only bytes,
closed without any successful capability; it is not overwritten, deleted or
adopted later by path. The legacy atomic-copy helper and production callers remain
unchanged. All live-proof, full-native-custody and publication flags remain false.
At A8b28, evidence persistence and bounded parent/primary custody were the next
gates; A8b29 below implements only the separate frontier persistence operation.

RCA: `docs/file-transfer-frontier-finalizer-gates-rca.md`.
Verification: `artifacts/frontier-semantic-copy-20260916/RESULTS.md`.

### A8b29: original frontier evidence persistence (implemented; production disabled)

The parameterless `TryPersistEvidence` operation consumes only its original
current semantic-copy/cursor/writer/recovery chain, completed processing and
sealed source. It generates the distinct, diagnostic-only
`filetransfer-frontier-finalizer-evidence.json` from immutable original facts.
The legacy ledger JSON, path-based finalizer and completion schema are unchanged;
this capability does not attest to that separate legacy JSON's persistence.

The fixed summary contains original run/scenario/ledger/generation scope,
sealed-source hash/counts/bookmark, initial/final observations, original four
output identities/hashes/counts/paths, writer sequence/drain facts and recovery
counts. It imports no JSON, scalar snapshot, destination, success flag or verdict.
It does not serialize private payloads, full origin maps or recovery tuples.
Those original retained inputs remain required by the live capability and later
handoff. Parsing this JSON can never recreate native ownership or authority.

The encoder bounds text to 4096 code units per field and the complete strict UTF-8
record to 64 KiB, with culture-invariant numbers and correct JSON escaping. This
is an artifact bound, not an expansion of the separate wire manifest or witness
budgets. Serialization, hashing, bounded 32-KiB writes, flush, rename and native
retention use the same cumulative five-second adopter work clock. An internal
finalization-lifetime accessor returns the original processing cancellation token
only for the original completed/EOF reader, never for an active, partial, copied,
foreign or retired reader. No new cancellation source or work allowance exists.

Persistence creates a unique temporary native file with read/write/delete access
and read sharing only. After durable file flush, handle-based no-replacement
rename moves that exact file to the fixed destination. A reduced read-only
duplicate retains original identity and write/delete exclusion; there is no
path reopen or gap between closing a creation handle and adopting its output.
This is successful local file flush/rename, not a power-loss directory durability
claim or cross-process publication. Existing destinations remain untouched.

Only the exact current `FrontierPersistedEvidence` validates this operation. It
continues to require the original copy and every preceding ownership gate, plus
its retained native output. Copied bindings/capabilities cannot consume attempts
or retire originals. Missing gates, repeat, closed inputs/output, over-bound or
invalid encoding, I/O faults, cancellation, budget expiry, original retirement
and legacy completion fail closed and release optional resources. Failure before
rename can leave a temporary file; failure after rename can leave final diagnostic
bytes. Neither can produce or revive a capability. Failed evidence is not deleted
or overwritten as cleanup. Resource cancellation callbacks acquire no GUI or
finalizer locks.

All live-proof, full-native-custody and publication flags remain false. No
production caller is added, no authority evaluation is replayed, and no route or
acceptance verdict changes. Next: original parent registration/pull/adoption/ack
and cleanup before legacy completion or GUI exit, followed by primary custody.

RCA: `docs/file-transfer-frontier-finalizer-gates-rca.md`.
Verification: `artifacts/frontier-evidence-persistence-20260916/RESULTS.md`.

### A8b30: original parent registration (implemented; production disabled)

2026-09-16. This checkpoint implements only the registration edge of the parent
handoff, not native pull/adoption or its acknowledgment. The exact original parent
request registers once after binding its actual live GUI process handle. Registration
rejects unbound, relay, self, copied, completed, exited or disposed requests.
Copied parent requests cannot bind, take or dispose originals. Repeating an original
registration retires only its optional record; it does not change legacy encoding.

The separate `frontier-parent-v1` endpoint has exactly 128 control bytes: magic and
version, monotonic registered/attached state, original request GUID, fresh nonce,
both process incarnations and SHA-256 of the original bounded request body. All
reserved bytes are zero and immutable. The original legacy body/header are still
checked independently. A nonblocking native mutex serializes attachment; no waits,
caller-supplied endpoint, process, pathname, manifest or success flags enter the
public registration/attachment APIs. Rejected foreign opens cannot cancel the
original record. This remains a trusted-workflow capability, not a security
boundary against arbitrary same-account native-memory manipulation.
GUI attachment uses the original cumulative five-second adopter work clock and
lock-free original scope checks, including parent-registration cancellation. No
fresh allowance, source/output scan or authority evaluation is added.

GUI attachment requires the original current persisted-evidence capability,
therefore the exact copy/cursor/writer/recovery/source chain. The GUI keeps its
original resources; no handle or evidence payload is exported. Copied bindings
and attachment capabilities cannot consume or revoke originals. Repetition,
scope/header corruption, cancellation, state regression, stale evidence, original
disposal and legacy completion fail closed and retire the optional set. GUI exit
also ends the parent's registration, not a completed native transfer of custody.

`GuiAttached` is only peer attachment evidence. It is not parent-side validation,
file adoption, acknowledgment, full finalizer approval or permission to release
GUI files. All live-proof/full-custody/publication flags remain false. The original
legacy completion schema and successful parent decoder remain unchanged; no
production caller, new authority evaluation or acceptance/verdict/count change is
introduced. Next: native pull/adoption with original inputs/origin maps, then exact
acknowledgment and cleanup before legacy completion/GUI exit; primary custody is
still a separate gate. No live-256 rerun is justified yet.

RCA: `docs/file-transfer-frontier-finalizer-gates-rca.md`.
Verification: `artifacts/frontier-parent-registration-20260916/RESULTS.md`.

### A8b31: complete original GUI native preparation (implemented; production disabled)

2026-09-16. Before publishing native descriptors to the A8b30 parent, the original
GUI must prepare its entire retained file set. A pathname list, the diagnostic
JSON summary, a subset of output handles or equal reconstructed metadata does
not establish that operation. `TryPrepareParentFiles` is parameterless and one-shot
on the original finalizer binding; it requires the original current A8b30
attachment and A8b29 evidence, hence all earlier original validation gates.

The native set contains exactly one sealed raw source, three retained processor
outputs, semantic analyzer copy, frontier evidence file, four original pipe
captures, and all private payload files from both original writer requests.
There are ten mandatory files and at most sixteen payloads per writer: 42 pins
maximum. Existing role-specific byte bounds remain unchanged. Only roles already
allowing empty files accept zero bytes. Distinct roles cannot alias the same
native file; payload observations are unique and matched by original object
reference to the exact native writer request slot, not adoption arrival order.

Pins use reduced-read-access duplication from retained native handles, never a
path reopen. Native file type, identity and exact length are checked before and
after duplication and during currentness. A partial or repeated native set is
not a preparation. The exact original writer manifests, stream capture and
immutable origin/validation maps, cursor observations, recovery markers, evidence
and parent attachment remain retained in the GUI. No facts are recreated from
the mutable public streaming result or JSON, and no content/hash pass is added.

The operation shares the original cumulative five-second adopter work clock and
processing cancellation token. No new work allowance/lifetime exists. Cancellation
callbacks close only resource handles and never acquire GUI/finalizer locks.
Missing/stale inputs, duplicate roles/files/observations, inconsistent writer
mapping, incomplete sets, native failures, repetition, closed pins, budget expiry,
cancellation and original/legacy retirement omit or revoke the whole optional
set. Copies cannot consume attempts or retire originals.

`FilesPrepared` is a local preparation subgate, not export, parent adoption,
acknowledgment or permission to release GUI resources. The 128-byte registration
protocol and legacy completion schema remain unchanged. All live-proof/full-
custody/publication flags remain false, with no new primary authority evaluation
or verdict/count change. Next: bounded original-parent transport/pull/adoption,
exact acknowledgment and cleanup while preserving these original mappings, then
primary custody and separately reviewed production opt-in. No live-256 run yet.

RCA: `docs/file-transfer-frontier-finalizer-gates-rca.md`.
Verification: `artifacts/frontier-parent-preparation-20260916/RESULTS.md`.

### A8b32: original-parent provisional native pull (implemented; production disabled)

2026-09-16. `TryPublishParentFiles` is a parameterless, one-shot operation on the
original finalizer binding, after current A8b31 preparation. It exports only that
set's native descriptors in canonical order: roles 0–9, then original payloads
in writer request slots 0 and 1. Each payload preserves its original observation
ID. No pathname, mutable public result, caller-selected handle or diagnostic JSON
is imported. The original preparation, exact facts/maps and all GUI pins remain
owned and current throughout this provisional step.

The separate endpoint is the A8b30 endpoint plus `.files-v1`. It has a fixed
2376-byte mapping and two manual-reset events (`.ready`, `.cancelled`), with no
blocking mutex/poll loop or synchronous acknowledgment wait. The 128-byte header
contains magic/version at 0/4, state at 8, SHA-256 of the original A8b30 128-byte
registration snapshot at 16, a nonempty submission GUID at 48, body length at 64,
and body SHA-256 at 96. Bytes 12–15 and 68–95 are zero. The original registration
snapshot binds its random nonce, original parent/GUI incarnations, request and
legacy request body; its initial state is zero on both peers. Legacy and A8b30
wire formats are unchanged.

The binary body is at most 2248 bytes: a 64-byte header (magic/version/count/zero,
GUI request and both distinct app requests), then exactly 52 bytes per file
(role, slot, observation GUID, GUI handle, volume/file identity, exact length).
There are exactly ten mandatory roles and at most sixteen payloads per writer,
42 total. Existing role byte limits and zero-byte allowances are unchanged.
Noncanonical roles/slots, duplicate native identities/handles/observations,
invalid scope/length/count, nonzero reserved bytes/tail and extra/truncated data
are rejected. Allocation uses local bounds. Opening validates one complete local
snapshot and its digest; all later checks compare the exact snapshot, except the
single monotonic state field. A rejected foreign open closes only its own handles.

| State | Owner | Meaning |
| --- | --- | --- |
| 1 Offered | Original GUI | Complete immutable descriptor packet and retained native pins; ready event is evidence only |
| 2 Pulled | Original parent | All native duplicates retained and checked; **not** a custody acknowledgment or permission to release GUI resources |
| Cancelled/retired | Either original owner/lifetime | Entire provisional set is unusable; no retry or partial-prefix salvage |

`FrontierParentRegistration.TryPullFiles` is parameterless and one-shot. It uses
only the original request's already-held native GUI process handle, not a PID
lookup or reopened path. Each duplicate requests only GENERIC_READ with no
source-close option, checks disk-file type/native identity/exact length, and is
owned immediately for rollback. Original request/process/registration/channel
currentness is rechecked throughout. Only the complete set can move to state 2.
No content read, seek, independent reader or additional hash scan is introduced;
the duplicates may share original file positions and cannot yet be read publicly.

GUI export stays within the existing cumulative five-second work clock and
original cancellation token. The parent has one separately bounded five-second
cooperative pull operation, covering packet open/decode, all native duplicates
and final publication checks; it is not reset per file and grants no additional
GUI work allowance. Native calls are not forcibly aborted. Its three resource-only
wait callbacks observe original GUI exit, original request closure and channel
cancellation, closing pins without waiting for a caller's currentness query.
Callbacks never acquire request/GUI/finalizer ownership locks. Repetition, copies,
missing/stale pins, mutation, partial native failure and legacy completion fail
closed; copies cannot consume or retire originals. Original GUI/request retirement
still revokes the whole pending set, including after a successful pull.

`FrontierParentFilesPending` deliberately exposes only currentness/counts. Full
origin arrays, cursor indices, frozen validation facts, original writer ranges
and recovery tuples have **not** crossed the process boundary. Their original
objects remain in the GUI; this packet is not a replacement for them. Next is
bounded transport and exact joining of those facts to the pending files, followed
by an exact acknowledgment/commit and reviewed post-GUI lifetime. There is no full
parent/primary custody, publication completion, authority evaluation, verdict
change, production opt-in or justification for a live-256 test yet.

Verification: `artifacts/frontier-parent-files-20260916/RESULTS.md`.

### A8b33: bounded original-fact transfer (implemented; production disabled)

2026-09-16. After A8b32's original native pull, the original binding may call the
parameterless, one-shot `TryPublishParentFacts`. Only the original pending parent
file capability can call `TryReceiveFacts`. There is no public fact/JSON/array,
path, scalar verdict or hash import. The GUI reads its original preparation's
immutable capture, cursor observations, manifests and recovery markers, not the
mutable public processor result. The GUI retains all original objects and pins.

The packet preserves the exact native descriptor body, run/scenario/source hash,
source byte/line counts and bookmark, verified range count, initial/final ledger
and generation observations (including read/committed bytes and synchronization
counts), every cursor line-start index, all three physical-origin maps, and five
output metadata records (processor outputs, semantic copy and persisted evidence).
It also preserves every frozen validation counter, final/prefix writer fact,
deferred batch, both original binary writer manifests with payload/range hashes,
and every recovery marker field, original stream/line position and deferred key.
Diagnostic paths are carried as text only and are never reopened.

The binary packet is capped at 4194304 bytes. Cursor indices are capped at 262144
64-bit entries and all origin maps together at 262144 32-bit entries: at most
3 MiB of index data. Strings use strict UTF-8 with explicit 32-bit byte lengths,
4096 code units per general string, tighter existing field bounds on decode, and
262144 aggregate encoded-text code units. Both original manifests retain their
32768-byte individual bounds. Writer/batch/marker counts remain 2/64/128, and
existing source/output/payload limits are unchanged. Encoding uses a fixed-capacity
buffer; decode checks every length/count before allocation and retains one private
validated copy. No new file-content pass or primary authority evaluation occurs.

The separate endpoint appends `.facts-v1` to the file endpoint. Its mapping is
80 + 4194304 bytes, with `.ready` and `.cancelled` events. The 80-byte header holds
magic/version at 0/4, state at 8, body length at 12, the SHA-256 of the original
A8b32 offer snapshot at 16, and fact-body SHA-256 at 48. This binds the original
submission GUID and native descriptor body, not just equal run strings. State 1
means offered, state 2 means received/structurally joined, not custody committed.
Padding is zero-checked on publication/open and never parsed; currentness rechecks
every actual header/body byte, with bounded 32-KiB scratch reads. Late padding
changes cannot become facts because body length and all parsed bytes are frozen.

Receipt checks the exact native body, source length, observation/generation and
bookmark agreement, ordered cursor indices, output native identity/length and
semantic-copy agreement, origin bounds/counts and exact contiguous retained slice
after the bookmark, unchanged successful validation
counters, exact two-writer/drain membership, prefix monotonicity, manifest request
slots, every payload observation/native identity/length and nonoverlapping original
ranges. Recovery records join their original stream slot's writer, unique paired
batch keys and exact immutable deferred tuples. No original GUI ownership receipt
is reconstructed in the parent: the private packet is provisional transport data.

GUI work uses the original cumulative five-second clock. Parent file pull and fact
receipt now share one cumulative five-second parent clock; receipt does not reset
or extend it. Existing file/request/GUI lifetimes remain mandatory. Copies cannot
consume or retire originals. Failed/repeated/expired receipt, mutation, stale
native pins, cancellation or original retirement revoke the full provisional set.
There is still no public reader, acknowledgment, independent post-GUI lifetime,
full custody, publication eligibility, runtime opt-in or changed primary verdict.

An initial implementation exhausted the unchanged GUI budget by repeatedly scanning
unused 4-MiB capacity inside nested ownership checks. Verification reproduced the
expiry at 5006 ms. The fix checks padding only at publication/open while retaining
complete actual-body currentness. No source budget, runtime cadence or throughput
policy was increased. Failed development runs remain recorded in the checkpoint.

At A8b33, exact acknowledgment/commit and verified post-GUI lifetime remained
absent. A8b34 below adds acknowledgment only; it does not grant that lifetime.
Verification: `artifacts/frontier-parent-facts-20260916/RESULTS.md`.

### A8b34: exact two-sided acknowledgment (implemented; production disabled)

2026-09-16. The exact original `FrontierParentFacts` may call parameterless,
one-shot `TryAcknowledge`. The original parent rechecks its registration, pending
native pins and complete immutable facts under the same cumulative five-second
parent clock, then issues a local `FrontierParentAcknowledgment`. Its private
owner requires exact object identity for both the acknowledgment and fact receipt.
Copies cannot consume or retire originals; no scalar/JSON/path import is exposed.

The separate file endpoint plus `.ack-v1` is exactly 80 bytes: magic/version at
0/4, state at 8, zero reserved bytes at 12..15, original file-offer snapshot
SHA-256 at 16 and original fact-header snapshot SHA-256 at 48. That fact header
contains the full body digest and exact body length. Both peers' captured fact
headers retain offered state 1; receipt changes only the shared view. The offer
already binds the unique submission, native descriptor body and original parent/
GUI request and process incarnations through the registration snapshot.

State 1 means the original parent retained and acknowledged the complete
provisional set. Only the original GUI binding's parameterless, one-shot
`TryConfirmParentAcknowledgment` opens that exact envelope and writes state 2.
It rechecks original source, preparation, facts and native resources under the
original cumulative GUI work budget. No synchronous peer wait is introduced.
The parent exposes `GuiConfirmed` only through its exact original capability.
Every envelope byte other than monotonic state remains immutable; rejected
foreign opens close only their own views, without cancelling the original.

Confirmation is a receipt barrier, **not a custody commit**. The GUI keeps all
original resources. Failure, replay, mutation, cancellation, legacy completion,
GUI exit or request retirement still revoke the entire optional set. The existing
native exit/request/channel callbacks still close every provisional parent pin.
No public reader, independent lifetime, full-custody/publication/live flag,
production caller or additional primary evaluation is introduced.

Boundary review found that simply retaining acknowledged pins on GUI retirement
would bypass existing gates: `Finalizer.Complete` retires the original binding
before encoding its legacy scalar summary; `TakeBodyAfterExit` retires pending
files before checking the held process's successful exit and decoding completion.
The summary is not the complete transferred packet and cannot establish its
custody. Neither method is changed by A8b34.

At A8b34, completion-bound commit and verified post-GUI lifetime remained absent.
The separate A8b35 transition below supplies that parent edge, not primary custody.
Verification: `artifacts/frontier-parent-acknowledgment-20260916/RESULTS.md`.

### A8b35: completion-bound parent custody (implemented; production disabled)

2026-09-17. Only the exact original confirmed acknowledgment may call one-shot,
parameterless `TryPrepareCommit`. It checks the original registration, complete
pending file/fact set and cumulative parent work budget, then duplicates that
exact parent-held native set into private read-only escrow. This adds at most 42
handles; no file is reopened by path, no source position is changed, and no file
contents are rescanned. Failure closes partial duplicates. The original pending
set and all its existing GUI/request/channel cleanup callbacks are unchanged.

The returned `FrontierParentCustody` is not current yet; its counts are zero and
there is no public reader. It retains the exact decoded immutable fact packet,
not a public result, reconstructed original GUI capability or legacy summary.
Copies cannot consume, dispose or activate the original. Request disposal closes
escrow synchronously, including during completion consumption.

The separate file endpoint plus `.commit-v1` is 80 + 65536 bytes. Its header is
magic/version at 0/4, state at 8, completion length at 12, the original 80-byte
acknowledgment snapshot SHA-256 at 16 and completion-body SHA-256 at 48. The
acknowledgment snapshot retains state 1 on both peers and already binds the exact
native offer, complete fact digest and original request/process incarnations.
Initial body/length/digest are zero. Rejected foreign opens close only local views.

| State | Writer | Meaning |
| --- | --- | --- |
| 1 Prepared | Original parent | Complete private read-only escrow retained; not usable |
| 2 Armed | Original GUI binding | Exact original facts/pins remain valid at the completion boundary; expected GUI retirement may release the provisional set |
| 3 Completed | Original GUI binding | Unchanged legacy `Finalizer.Complete` succeeded, and its exact bounded body was published; still not usable before original parent take |

The parameterless `TryCompleteParentHandoff` captures scalar inputs only from the
original preparation's immutable capture, ledger origin and writer facts. Opening
and arming the channel use the original cumulative GUI work interval. It then
calls the existing finalizer completion method, which still retires its original
binding before validation/encoding. The completion/publish tail must fit the
existing 250-ms completion bound. Success releases the GUI channel view without
cancelling the parent endpoint; any failure cancels escrow. There is no synchronous
peer wait inside an ownership lock.

GUI exit alone cannot activate custody. A resource-only native wait closes escrow
on nonzero exit, missing/invalid completed state or channel cancellation. Original
request closure cancels escrow unless the exact original successful post-exit
take has begun consuming its own legacy completion; request `Dispose` always
cancels directly. The original wrapper still calls `TakeAfterExit` only after
successful exit, completed output capture and persisted finalization state. No
new scalar output/EOF-success override or alternate consumption method exists.

The existing one-shot `TakeAfterExit` retains its original native exit/code and
legacy decode gates. Its optional custody hook also checks the original parent/
GUI scope and request bytes, the exact committed body, private immutable ledger/
bookmark/source-line facts, three original native output identities/hashes/sizes/
line counts, exact analyzer-copy metadata/path mapping and original writer closure
map (the distinct native/legacy filename mapping is specified in A8c35 below).
Only then can the parent capability become current across GUI retirement. Failure
closes escrow without altering an otherwise valid legacy completion result. A
repeated original take revokes custody; copied requests cannot take or revoke it.
Post-acceptance channel or native-file mutation revokes custody on currentness.

Two initial positive fixtures failed on an incorrect path comparison: the old
decoder synthesizes fixed role names for its first three outputs, whereas the
owned capture preserves unique creation names. Those legacy labels are not native
locators. The corrected join uses exact native identities and content metadata
for those roles; the original creation paths remain in the fact packet, and the
actual analyzer-copy path was still checked exactly. A8c35 below updates that
comparison for A8c34's distinct native filename. No legacy schema, file name,
path-based reopen, processing budget or runtime policy was changed to pass tests.

This is parent resource custody only. Full-native-writer, publication and live
eligibility flags remain false. Native duplicate handles share file positions.
A8b35 itself supplied no primary input reader/owner or permission to infer one
from legacy completion. A8c1 below supplies request-side transfer and private
readers; native worker/input/invocation ownership, the 64-witness limit and
unchanged evaluation count remain subsequent gates. Production opt-in and
live-256 remain separately gated.
The handoff must consume the actual parent-held handles before the owning
request is disposed; the old GUI handle numbers in the fact packet are historical
mapping data, not live post-exit handles.
Verification: `artifacts/frontier-parent-commit-20260917/RESULTS.md`.

## A8c1: primary-request native custody and private readers (implemented)

The exact A8b35 custody may run `TryHashPrimarySource()` once, using the
retained analyzer-copy handle for the optional primary's original source hash
pass. This replaces `HashAndHold(path)` on that future optional path; it is not an
additional pass. Read-only mapped windows preserve the shared native position.
The resulting lease retains the original restrictive native file object. Its
read duplicates do not loosen sharing or reopen a path. Legacy leases retain
their original `ReOpenFile` behavior.

`TryTransferToPrimary(originalRequest)` consumes custody only into the original,
unbound `LocalRetainedSourceRequest`. The request must retain the exact accepted
completion object, original request header/body and same native analyzer-copy
identity/length. An equal/copied completion is insufficient. The transfer and
native hash share the existing cumulative 5000-ms parent work budget. An actual
failed transfer retires its old custody and partial duplicates; copied custody or
requests cannot consume the originals. A successful transfer retains the complete
read-only set and exact private full fact packet before retiring the old set.

The new `FrontierPrimaryInputs` belongs to that original primary **request in the
parent process**, not yet to the primary worker or an evaluator. It survives old
GUI-parent request and hash-lease disposal. Its own request disposal/cancellation,
held worker exit, scope mutation, invalid native pin or exhausted work budget
closes all readers and pins. Native callbacks take only resource locks.

Private readers are one-shot per native entry, read at most 32768 bytes per call,
and map only the requested window. Each maintains its own monotonic position;
there is no public stream, seek, descriptor import, line/origin remapping or EOF
authority token. Empty files return EOF without creating a mapping. The complete
set remains bounded by 42 pins and at most 42 additional reader handles. Active
reader preparation/traversal shares one 20000-ms budget using the existing
producer limit; idle time is excluded. Repeated/invalid reads fail closed.

Currentness uses the request's once-retained `SafeFileHandle` reference. Repeated
`FileStream.SafeFileHandle` getter calls can synchronize a cached position on
PowerShell 7; the native position regression covers the source hash and all input
readers on both hosts. Mapping itself does not advance the shared position.

A8c1 alone did not deliver this set to a primary worker. A8c2 below adds that
native attachment; original input/view association remains a subsequent gate.
All full-native-writer/publication/live flags remain false.
Verification: `artifacts/frontier-primary-request-20260917/RESULTS.md`.

## A8c2: original native primary-worker attachment (implemented)

The original request-owned input capability may call parameterless
`TryPrepareWorkerHandoff()` once, after an actual worker distinct from the parent
is bound and before any parent-side private reader is opened. Once preparation is attempted, further
parent reads fail closed. Existing publication requests are excluded: this is
not an alternate entrance to their completion issuer.

Preparation reuses the bounded native-file and fact channel formats under the
request-local `.frontier-primary-v1` endpoint. Their binding starts with SHA-256
of the exact post-bind primary header/body, including original parent and worker
process incarnations and request identity. Facts remain bound to the complete
native offer snapshot. No original GUI packet or wire schema is rewritten.

The exported native offer uses the **current parent-held handle numbers**.
The immutable fact packet still contains the historical GUI descriptors; the
worker compares every role, slot, observation, contributor identity, native file
identity and length between the two packets, ignoring only that deliberate handle
number rebasing. Historical GUI numbers are never used for native duplication.

`TryAttachFrontier` is an explicit alternate attachment API sharing the original
one-attempt worker admission gate. It verifies original request/process/source
scope and retains the actual parent process handle with duplicate-handle access.
Each imported handle is downgraded to read-only and checked against the exact
native identity/length before the complete set is retained. The worker also
joins decoded immutable facts with its original request's completion and source,
including the original GUI request identity. Frontier attachment rejects a
parent/worker self-binding independently of parent preparation.
Both channel receipt states advance only after all local pins and facts agree;
receipt states alone are not evaluator or publication authority.

The original request view stays pinned and byte-exact checks continue after
attachment. Copied workers/capabilities cannot act on originals. Cancellation,
changed scope/channel/file state, disposal and process exit invalidate resources;
callbacks close only resources and do not acquire owner locks. Partial pulls
dispose acquired handles. Parent preparation shares A8c1's active work clock;
worker attachment has its own bounded active preparation interval, including
request-snapshot construction, using the existing 20000-ms producer limit. No
file-content scan is added by attachment.

A8c2 alone exposed `FrontierWorkerInputs`, not a completed source reader or
input/view association; it refused source opening rather than falling back to a
path. A8c3 below adds native source consumption. Legacy attachment remains
separate; full-custody/live/output flags stay false.
Verification: `artifacts/frontier-primary-worker-20260917/RESULTS.md`.

## A8c3: native primary source and physical-origin ownership (implemented)

An original frontier worker's one-shot `TryOpenSource` now creates the existing
`SourceReader` type from its retained semantic-copy handle (native role 4). The
reader duplicates read-only access, uses private 32-KiB mapped windows and never
reopens a path or seeks the shared native file position. Open/read/origin work
shares the existing worker's cumulative 20000-ms active-work clock. No additional
content or hash pass is introduced.

The semantic copy is not the raw LF-terminated ledger: its existing writer puts
CRLF **between** records, without a final separator. The bounded strict-UTF-8
framer therefore has an explicit final-record mode used only here. The original
raw-ledger caller retains its required final LF. BOM/CRLF handling, line/byte
ceilings and malformed UTF-8 rejection remain unchanged. Native EOF additionally
requires the original semantic line count and complete source byte consumption.

The immutable fact decoder now retains its already-validated semantic-origin
array; wire bytes and validation rules are unchanged. The reader privately keeps
the exact string references emitted during its one pass, within existing record
and cumulative code-unit limits. After admitted EOF, `GetFrontierPhysicalOrigin`
joins only an exact original reader, valid semantic ordinal and that ordinal's
original string reference to the physical raw-ledger line. An equal replacement,
wrong ordinal, copied reader, partial read or stale owner cannot create that join.
This lookup is source provenance, not input-array or evaluation authority.

Original reopen/re-enumeration, early iterator disposal, cancellation, stale
scope/facts/pins, work exhaustion and explicit disposal retire the optional set.
Copied readers/workers cannot consume or dispose originals. Resource callbacks
close the mapped stream and clear retained line references without owner-lock
acquisition. Source ownership remains dependent on all original native pins and
request/channel lifetime after EOF.

Frontier workers still reject legacy input capture/reference validation,
`TryBeginPrimary` and writer-path opening even after source EOF. This prevents
source consumption from implicitly enabling old evaluation/publication paths.
Production orchestration, full/compact arrays, original views/invocation identity,
the 64-witness cap and diagnostic output are not changed here. Next: bind exact
native source origins to the original arrays and views, then issue a separate
frontier-diagnostic capability.
Verification: `artifacts/frontier-primary-source-20260917/RESULTS.md`.

## A8c4: native array/view association primitive (implemented)

The original frontier worker may call `TryBeginFrontierPrimary` once with its
original completed native reader, private invocation context, original full and
compact arrays, and their semantic-copy ordinals. Each ordinal is only a lookup:
the corresponding array entry must be the exact string emitted at that position.
Both maps must be strictly ordered, and compact must be an exact ordered subset
of full. Physical origins come only from the retained immutable native facts.
The capability keeps the original arrays, private string-reference snapshots and
private physical-origin maps; it does not retain caller-owned ordinal arrays.

The separate `FrontierWorkerInputs.EvaluationCustody` records the original full
result, then the original compact result, then issues one exact `EvaluationPair`.
The pair has distinct generated invocation identities and the original request
identity. Identity checks require the original context, arrays, views and issued
pair, not equal objects, copied capabilities or copied scalar identities.
Origin lookup also requires that exact pair and its original array/view pair.

Every successful record/take/ownership/origin use rechecks both arrays against
their private reference snapshots and the original native request/file/source
lifetime. Capture, subset and reference-check visits share the existing producer
record ceiling, including the already-read source count; active work shares the
original worker's 20000-ms clock. No content/hash/source pass or evaluator call is
added. Repeated begin/record/take, invalid original operations, mutation, exhausted
work and native revocation retire the optional set. Foreign ownership queries
and copied owners cannot retire the original. Cancellation cleanup clears stored
arrays, views, maps and pair references under the resource lock only.

This is a private-primary association primitive, not independent authentication
of evaluator execution or catalog completeness. Its trusted future caller must
use the actual original assembly/evaluation seam; a caller holding that private
capability must not supply invented views. Whole mutable evaluation objects are
not frozen by identity checks. A later bounded diagnostic preparation must join
and capture the relevant original failure fields and native input descriptors.
The primitive itself neither selects failures nor changes evaluation results.

Production orchestration remains unchanged and has no caller of the new API.
The legacy capture/custody/writer/publication entry points remain blocked for
frontier workers. All full-native-writer/publication/live flags remain false.
The next edge is original-failure witness preparation (maximum 64) and its exact
native input/payload joins, followed by the distinct frontier-diagnostic output
capability. This is not approval to emit diagnostics, alter correctness or run
live-256. Verification: `artifacts/frontier-primary-evaluations-20260917/RESULTS.md`.

## A8c5: evaluation-bound native payload capture (implemented)

Review of witness preparation exposed a prerequisite: the existing detached
offline witness resolver accepts receipt JSON, opens the legacy writer path and
expects the offline injected-clock receipt profile. It cannot establish the
native payload/source/first-failure join for A8c4. It remains unchanged and is not
enabled for frontier workers.

The immutable parent-fact decoder now retains its two already-decoded and
validated writer manifests. There is no wire change or second fact decode. Their
payload order is the exact order previously joined to native entries 10 onward;
historical GUI handle numbers are not used to read files.

The original `EvaluationCustody.TryCapturePayloads` requires its one issued pair,
original context, arrays and views. It rechecks both arrays and all native scope
before and after capture. Each payload is read once from the corresponding held
read-only native pin, using private mapped windows of at most 32768 bytes. It
checks original length, exact SHA-256 and strict UTF-8 before retaining text.
Temporary mappings/duplicates are disposed and byte buffers cleared on every
exit. This is the primary's first payload read/hash, not a source reread or another
authority evaluation. All work shares the existing active-work clock.

Limits remain 16 payloads and 4 MiB per app, 262144 bytes per payload and two apps
(at most 32 payloads / 8 MiB overall). These are native catalog limits, not a change
to the separate 64 physical-witness limit. Capture is all-or-none and one-shot;
partial failure or a repeated original capture retires the optional set.

The resulting `NativePayloadSet` belongs to the original custody and pair.
`GetPayloads` returns a copied array of original row objects, not a mutable view
of the private catalog. Currentness and row ownership require the exact original
set/row and revalidate the original arrays/native lifetime. Copied sets/rows,
foreign pair/context/array/view arguments and pre-take calls cannot acquire or
retire original ownership. Row scalars and previously copied text are diagnostics
only and cannot reconstruct that capability.

Native cancellation, source/evaluation/worker disposal, changed facts/scope/pins,
array mutation and exhausted budgets invalidate the entire set. Resource-only
cancellation clears cached text from the original rows without acquiring owner
locks. Clearing cannot revoke a string already copied by a caller; such a string
was never authority. No path-based reopen, legacy writer admission, evaluator,
receipt-schema interpretation, failure selection or publication issuer is added.

This is the native payload prerequisite, not completed witness preparation.
The next edge still must join the original reported failure, exact native receipt
profile/links, completion and original full/compact view evidence; capture native
input descriptors and at most 64 physical witnesses in a bounded batch. Relevant
contradictions must remain in scope. Production and live-256 remain disabled.
Verification: `artifacts/frontier-primary-payloads-20260917/RESULTS.md`.

## A8c6: evaluation/payload-bound physical witness batch (implemented)

The original fact decoder now retains its already-validated raw line-start index
and source byte count. There is no wire-format change, second parse or source
reread. The primary can capture one batch of 1–64 strictly increasing one-based
full-array positions, using the original A8c4 pair/context/arrays/views and an
exact original A8c5 payload set and row. It snapshots the selection and derives
physical source lines, raw byte offsets/counts and compact-array positions from
private original maps, never from caller line labels. Each selected text is the
original emitted string; its SHA-256 covers strict UTF-8 text without a delimiter.
Raw byte counts include the source line delimiter and are not that hash domain.

The same batch binds the selected payload's native completion and marker ranges
to exact physical line boundaries and writer-sequence counts. Windows writer
receipts hash `PersistedText` without the final CRLF; the cursor contains that
exact two-byte suffix. The join requires the next indexed line (or source EOF)
at `Offset + Length + 2`, not an approximate byte tolerance. Range hashes remain
the original manifest's raw/batch hash domains, not selected-line hashes.
Range descriptors do not establish selected-row coverage; the later semantic
join must include every required physical witness within the same 64-row cap.

Capture checks original arrays/native custody before and after the bounded work.
Invalid selection, repeat, malformed range, exhausted work/record budget or
revocation retires the optional set. Copied/foreign capabilities do not consume
the original. Exported row arrays are copies; original batch/row ownership and
currentness remain reference-bound. Cancellation and disposal clear original
cached text; copied strings and immutable scalar diagnostics cannot be revoked
and never confer authority. The existing active-work/record limits are unchanged.

This is a physical-selection primitive, not completed original-failure witness
preparation. It does not establish that the caller selected the original reported
failure, interpreted the native receipt schema, preserved all contradictions or
matched its completion to full/compact view evidence. Those semantic joins,
bounded relevant-field snapshots, input descriptors and separate diagnostic
output remain required. No evaluator, legacy writer reader, publication issuer
or production caller is added; every publication/live flag remains false.
Verification: `artifacts/frontier-primary-witnesses-20260917/RESULTS.md`.

## A8c7: native receipt-to-original-failure diagnostic join (implemented)

The trusted primary supplies its already selected original failure, original
candidate collection and existing recent-grace proof, plus the exact A8c4 pair
and A8c5 payload set. The separate native resolver does not call the correlator,
evaluate authority, select a replacement failure or import caller receipt JSON.
It requires exact failure/event membership in the original unresolved records,
unchanged relevant full/compact view fields and an unambiguous native receipt.

Selection runs within the original native lifetime and cumulative work/record
budget. Native receipt source, repair continuity, recent arm, stall root and
successor, write, frame, accepted completion and marker links are joined to the
original emitted lines. Completion evidence must be the same original line in
both views, and its physical line must match the chosen native completion range.
The marker must match its native range and completion-batch identity. Original
semantic ordinals are derived from the retained origin map, not caller labels.
The original stream boundaries and relevant episode diagnostics remain selected
within the same 64-physical-witness cap; overflow omits the entire optional result.

Required proof links reject duplicate fields. An existing forced-state diagnostic
repeats `range_fingerprint`; opaque coverage preserves that exact original line
without using it to satisfy a required link. Writer/event and session/transfer
identities remain unique. No runtime logging format is changed. Native marker
publication follows stream closure; held snapshot emission can place the state
send diagnostic before the completion snapshot. No invented reverse ordering
is imposed on this native profile.

The original batch retains a bounded, immutable diagnostic snapshot (at most
16,384 UTF-16 code units) of the relevant failure/view/completion fields. Invalid
joins, replay, overflow, array mutation or native revocation retire the optional
set; retirement clears the batch's snapshot and cached witness text. Foreign
context queries do not consume the original. The exported PowerShell wrapper,
copied strings and scalar fields are diagnostics, never publication capabilities.

This remains a trusted-primary diagnostic seam, not authentication of arbitrary
caller-built proof objects or a general receipt-JSON validator. Batch currentness
rechecks native custody and original arrays; it does not automatically detect
later mutations inside the original mutable proof/view objects. A later output
and final-publication gate must revalidate those relevant fields against the
captured snapshot. Native input descriptors and a separate parent output
capability are still required. No production call site, legacy completion issuer,
primary failure/count, Recovered/Superseded, acceptance or route behavior changes;
all publication/live flags remain false. Verification:
`artifacts/frontier-primary-join-20260917/RESULTS.md`.

## A8c8: original-state guard and local diagnostic preparation (implemented)

A8c7's deep-mutation limitation is now closed for the relevant observed fields.
Before semantic resolution, the native owner captures the original failure/event,
candidate and unresolved-record membership, index line references, view metadata,
family arrays and completion-boundary fields. It checks that state again after
resolution and physical capture. Subsequent original-custody currentness checks
also revalidate it. Mutated/replaced note properties, relevant values, array
members, original event lines or completion fields retire the optional set.

This guard accepts only exact PowerShell note properties, ordinary one-dimensional
zero-based arrays and the specified string/int/long/bool scalar fields. It never
executes script properties, custom collection accessors, replacement-object
`Equals` methods or retained PowerShell callbacks. At most 65,536 field/array-item
observations are retained; array size is checked against remaining capacity before
allocation. Traversal is charged to the existing cumulative record/work limits,
and existing text limits apply. This is relevant-field observation of the trusted
primary's original objects, not authentication of caller-authored proof objects,
an evaluator rerun, a whole-object freeze or a history of intervening mutations.

Only an original semantically joined batch can issue one local
`NativeDiagnosticPreparation`. It binds the exact original invocation pair,
payload and witness batch; request/worker incarnation; existing scope, file-offer
and fact-receipt bindings; and the original source digest. Input descriptors are
derived directly from retained native file entries, with role, writer slot,
observation identity, native file identity and byte count. No paths, handles or
caller descriptor imports are exposed. Exported descriptor arrays are copies and
their rows are immutable diagnostics, not ownership credentials.

Preparation and descriptor access recheck native lifetime, original arrays and
the new relevant-field guard. Replay, disposal, changed state, exhausted budget
or native cancellation retires the optional set and clears the original cached
preparation snapshot/descriptors. Copied batches/preparations cannot acquire or
retire the original. A physical-only A8c6 batch cannot prepare diagnostics.

This is local preparation, not the parent output channel or publication. Final
publication must still call the original guard at its own boundary, with no
intervening mutable wrapper treated as authority. Bounded output serialization
and original parent escrow/adoption must precede expected-worker exit; one-shot
consumption must follow the required exit/output/source/terminal gates. These
are the next edge, without weakening existing worker-exit cleanup. No legacy
completion schema/issuer, production call site, failure/count, acceptance or
runtime behavior changes; all live/custody/publication flags remain false.
Verification: `artifacts/frontier-diagnostic-preparation-20260917/RESULTS.md`.

## A8c9: separate native diagnostic transport (implemented; production disabled)

The original parent prepares one independent escrow from its complete retained
native input set after binding the exact worker and creating its input handoff.
It retains at most 42 read-only input handles and its own exact request-scope
view. Existing primary-input cleanup on worker exit is unchanged. This separate
escrow, not worker-local objects or returned JSON, supplies output lifetime.

Only the original A8c8 preparation may offer the binary diagnostic packet. It
contains original request/worker/incarnation and invocation identities, scope/
offer/fact/source digests, native-derived input descriptors, the diagnostic
snapshot, exact selected payload and 1–64 physical witnesses. No caller packet,
path, descriptor or handle import is exposed. The fixed wire payload is capped
at 4 MiB; exceeding it omits the whole optional diagnostic. Existing snapshot,
payload, witness, source and worker work/record limits are not enlarged.
Strict UTF-8 and length checks apply before decoding text allocations.

The original parent adopts once while that worker is alive and input custody
is current. It validates the packet against its original immutable native facts
and retains private copies of the exact header/body. The worker then checks
the original arrays, relevant proof/view state, native lifetime and exact
adopted bytes again before committing. A private random token is committed in
the offer and revealed only at final commit; changing a shared state word alone
does not satisfy the parent take. The token protects this handoff sequence, not
against arbitrary code execution in the trusted worker or same-user process
tampering with private memory.

One `TakeAfterExit` may issue an original `FrontierDiagnosticTransport` only
after the same held native worker exits with code zero and the frozen packet
and commit token still agree. This is transport receipt, **not terminal-report
acceptance**. Successful worker disposal may close the old input channel without
retiring committed parent escrow. Failed/no-commit exit, malformed/partial data,
mutation, replay, exhausted budget, closed escrow pins or original request
cancellation omit the diagnostic. Request cancellation/disposal still closes
escrow after take and clears its original cached bytes/snapshot. Copied receiver,
preparation and transport capabilities cannot acquire or retire originals.

Worker encoding/publication uses its existing cumulative 20-second budget.
Parent adoption, take and bounded exports share one five-second active-work
budget; idle process waits do not replenish or consume it. Escrow preparation
uses the existing parent input-work budget. No new source scan, authority
evaluation, legacy completion schema or publication-completion issuer is added.
Exported byte arrays are copies and remain non-authoritative after revocation.

The remaining gate is to join this exact original transport with original
primary stdout/stderr EOF, source and terminal-report acceptance. No caller
success boolean or parsed JSON may replace those gates. All live/full-custody/
publication flags remain false; production calls, primary failures/counts,
Recovered/Superseded, route and acceptance behavior remain unchanged.
Verification: `artifacts/frontier-diagnostic-output-20260917/RESULTS.md`.

## A8c10: original primary pipe-output ownership and join (implemented)

Inspection of the terminal path exposed an earlier prerequisite: its original
stdout/stderr reads belong to caller tasks, so a frontier consumer could not
authenticate EOF by receiving task objects, strings or success booleans. The
new opt-in `TryStartFrontierWorkerWithOutput` starts an unstarted process from
the original unbound primary request after native input custody is established.
It requires both redirected streams and owns their first reads. No late bind,
existing process, caller reader/task, output path import or EOF override exists.
The existing `BindWorker` and caller-task path are unchanged.

This reuses the existing native raw output pumps and retained output handles;
there is no second capture implementation or decoding/re-encoding step. The
two streams pump independently through fixed 32-KiB buffers. Retained custody
is capped at 64 MiB per stream (128 MiB total), with at most four write-phase/
read-only handles during sealing and two read-only handles after EOF. Ordinary
artifact capture continues to EOF under the caller's existing outer deadline;
the retention cap does not truncate output or replace that process deadline.
Capture failure or optional revocation continues draining live pipes and does
not kill the worker. A failed launch operation may have started a process before
a later gate fails; the caller remains responsible for that exact process.

One `TryJoinDiagnostic` associates the exact original A8c9 transport with this
original output owner only after successful exit of the held native worker,
successful EOF/capture of both pipes, current request/source scope and current
diagnostic escrow. Process exit alone is insufficient when an inherited writer
keeps a pipe open. An attempted join without EOF is omitted permanently; late
closure cannot revive it. Copied owners, joins and transports cannot acquire or
retire originals. Replay, pin closure, changed scope and original cancellation/
disposal revoke the join. Request cancellation closes retained output pins even
without another consumer call. The actual pumps retain their existing lifetime.

This is output ownership plus transport association, not terminal-report
acceptance. The original receipt writer, source/result agreement, successful
atomic receipt write and exact returned receipt identity remain the next gate.
The existing parents render decoded stdout/stderr using `Set-Content -Encoding
UTF8`; these new captures retain raw bytes. The next gate must preserve existing
report encoding/framing and must not equate raw-capture and report hashes or
silently replace the original report artifacts.
No terminal-writing hook, primary report/failure/count, evaluator, source scan,
legacy publication issuer or production call site changes. All live/full-native-
custody/publication flags stay false. Verification:
`artifacts/frontier-primary-output-20260917/RESULTS.md`.

## A8c11: original terminal receipt binding (implemented; production disabled)

The original output launch can capture the parent's route/start context before
starting the worker. Its exact A8c10 join then issues one private terminal
custody capability. The two-argument launch remains compatible and cannot issue
terminal custody. No production parent opts into the new launch or receipt path.

The original receipt writer accepts that exact native capability separately
from legacy publication adoption. It prepares immediately before its unchanged
atomic JSON write, records only after that write succeeds, and permits one take
using the exact returned object. Preparation binds original request/source/run/
scenario identity through the exact original source-request object as well as
its native metadata; a missing or foreign source cannot prepare or consume it.
It also binds worker identity, original pre-launch route/start context, unchanged
source hash/count, native successful exit, canonical artifact paths/hashes and
the original full-view catalog. The internal diagnostic binary packet is now
version 2: it carries five bounded catalog scalars directly from the original
guarded full view. Version 1 is rejected; the 4-MiB cap and all existing budgets
remain unchanged. No parent parses diagnostic JSON to recover this binding.

Raw stdout/stderr hashes are not report hashes. The gate reads each sealed raw
capture once through bounded buffers, using the original Process reader encoding
with BOM detection, then hashes the existing `Set-Content -Encoding UTF8` text
projection (host-specific UTF-8 BOM and appended platform newline). It compares
those hashes with the original writer's report hashes. It does not replace or
rewrite reports, create another capture, or retain whole decoded output strings.

The native source's restrictive file object cannot be reopened by the legacy
path reader. A separate opt-in source request therefore supplies read-only
mapped streams to the receipt's existing hash and line-count functions. These
are the same two original passes, not extra scans or cached claimed results.
No sharing permissions are widened and the source's native position is unchanged.
This read capability grants no terminal or publication authority and remains
separate from optional diagnostic omission (including exhausted terminal budget).
The path-based receipt writer remains unchanged when no native source is supplied.

Only built-in ordered dictionaries, bounded scalar values and original returned
note properties are inspected. Copied capabilities/returns, replaced properties,
mutation, replay, wrong bindings, missing reports, failed writes, cancellation and
closed native pins cannot acquire or retain acceptance. At most 64 receipt fields,
11 two-field artifact records, 32,768 text units and five seconds cumulative active
terminal work are retained. The existing source passes retain their original
primary lifetime; output hashing is capped by the existing 64-MiB-per-pipe limit.

The result records the original `completed` or `proof_incomplete` status; it does
not upgrade a failed primary or replace existing completed-proof checks. All
live/full-native-custody/publication flags remain false. The legacy completion
issuer, receipt schema, primary verdict/counts and evaluation count are unchanged.
The trusted original writer is the write-success authority, not an arbitrary
formatter or serialized receipt. This is not a defense against arbitrary code
execution inside that trusted parent. Production integration, optional diagnostic
persistence and final live validation remain separate work.
The terminal result is current only while its original request remains alive.
Future persistence must consume it before original parent cleanup; returning a
revoked wrapper after `finally` cannot substitute for that operation.

Verification: `artifacts/frontier-terminal-receipt-20260917/RESULTS.md`.

## A8c12: terminal diagnostic persistence before cleanup (implemented; production disabled)

Only the exact current A8c11 terminal receipt can attempt persistence, once,
before its original parent disposes the source request. No caller destination,
JSON, packet, receipt copy or success flag is accepted. A copied receipt cannot
consume the original attempt. Replay retires the prior persistence capability;
it cannot overwrite or reissue a saved result.

The fixed destination is `filetransfer-frontier-terminal-diagnostic.json` in the
original launch directory. The separate schema-v1 diagnostic record contains
native-bound attempt/run/scenario/source, original terminal status, worker and
invocation identities, witness count, catalog and stdout/stderr report hashes,
and the byte-exact original version-2 diagnostic packet as base64 with its count
and SHA-256. The packet retains the original witnesses/payload/snapshot; there is
no second selection, source scan or authority evaluation. Metadata is serialized
from the private original snapshot, never reconstructed from receipt JSON.

The original 4-MiB packet and 64-KiB metadata-encoder bounds imply an envelope
below the separate 6-MiB persistence cap. The existing finalizer-evidence path
keeps its 64-KiB cap. The shared native primitive uses create-new, bounded writes,
flush, exact-handle rename without replacement, and retained read-only custody.
Ownership, original receipt properties and the same cumulative five-second
terminal work budget are checked throughout the operation. No budget is reset.
Failure may leave diagnostic-only temporary or post-rename bytes, never a current
success capability. Existing destinations are preserved, not deleted or replaced.

The saved capability remains tied to the original terminal/request and exact
native output file. Copies, closed file pins, receipt mutation, cancellation,
replay or cleanup cannot retain it. Parent cleanup releases the native file;
the JSON remains a historical diagnostic, not a portable or importable authority
token. `proof_incomplete` stays `proof_incomplete`; all live/native-full-custody/
publication flags are false. Optional persistence failure does not change the
original receipt status or writer outcome. No legacy completion issuer is used.

Both original production parents remain unchanged. Their old `Start` plus
`ReadToEndAsync` report path cannot simply follow the native pipe launch: that
launch may have started the process and acquired the pipes before returning
null, and optional capture write failure drains/discards subsequent bytes.
The next integration step must preserve ordinary primary reports independently
of optional custody failure, including exact host encoding, failed/nonzero
launches, inherited-pipe EOF and the existing deadline/cleanup behavior. It must
not start a worker twice or compete for the same pipe reads. Production opt-in
and any live-256 validation remain disabled pending that focused parent work.

Verification: `artifacts/frontier-terminal-persistence-20260917/RESULTS.md`.

## A8c13: primary reports independent of optional capture (implemented; default off)

`StartFrontierWorkerWithReports` separates the original report tasks from optional
native output custody. An exact original request starts one unstarted redirected
process. Invalid redirects and copied/repeated launch attempts are rejected before
launch; an original replay also retires prior diagnostic custody without stopping
ordinary reports. A real process-start failure remains an error. Optional capture creation
or native binding failure does not return an ambiguous null after launch: the
parent receives the original stdout/stderr report tasks and the source-binding
outcome, with absent diagnostic output when the optional lane failed.

When native input custody is available, the report reader owns each first pipe
read and tees those exact raw bytes into the existing capture. A target write,
flush or retained-file seal failure omits diagnostic custody but continues the
original decoded report. Actual pipe-read errors remain report-task failures.
Revocation/request cleanup never closes the process-owned live read pipes. The
normal report strings retain the original `ReadToEndAsync` behavior; the 64-MiB
retained-custody bound does not truncate reports. There is no second competing
reader and no path-based reconstruction of authority. Legacy raw GUI capture is
unchanged apart from sharing the now-partial native capture type.

Decoding uses the original process reader encoding with BOM detection. The
parents still perform their original `Set-Content -Encoding UTF8`, including
host-specific BOM and final newline, and preserve exit/status/proof checks,
source passes, watchdogs and cleanup. Native exit is not pipe EOF: a descendant
holding a pipe keeps that report task incomplete and cannot provide a native
output join. Ordinary report tasks and the source-bound boolean are not authority.

Both original retained-analysis functions now have an explicit internal
`CaptureFrontierReports` switch. No production call site passes it. Missing
source ownership and every default caller use the old launch/read branch. The
new branch never falls back to a second launch after a started-process failure.
It does not activate capture/export, change the primary evaluation count, issue
legacy publication completion, or wire receipt/persistence hooks by itself.

Next: connect the original native source/custody handoff, live parent diagnostic
adoption and exact terminal receipt/persistence inside both original parent
lifetimes. Preserve the existing source passes and primary outcomes; do not
reconstruct native inputs from paths or summaries. The new report branch remains
default-off until that complete parent integration is verified. No live-256 run
or correctness/route approval follows from report preservation alone.

Verification: `artifacts/frontier-primary-reports-20260919/RESULTS.md`.

## A8c14: original pre-analysis source count (implemented; parent wiring later)

The native source's restrictive sharing prevents the original pathname-based
line-count pass from following its native hash pass. `CountFrontierSourceLines`
now reads the exact original native hash lease through a read-only mapped view,
without reopening the path or moving the shared native file position. The existing
`Get-FileTransferEvidenceLineCount` accepts that lease through an explicit optional
`ReadLease` parameter. Default pathname and terminal-owned reads are unchanged.
The hash pass retains its original handle reference once. Counting duplicates
that handle into a fresh read-only stream before mapping: fetching the old stream's
`SafeFileHandle` again can synchronize a stale cached position on PowerShell 7.
A regression moves the native position after hashing, then verifies it is unchanged
by counting on both hosts.

This is the original pre-analysis count, not another source/hash scan or a count
copied from completion metadata. It uses the original strict UTF-8 decoder, BOM
detection and `ReadLine` semantics, including malformed-input failure. Only the
exact original native lease can count once. Foreign paths, copied or ordinary
leases and competing source arguments are rejected; closed pins and disposed
leases cannot complete. Rejected foreign calls and count replay do not retire the
original source owner. Counting does not create input/evaluation/terminal authority.

The genuine headless chain exercises native hash, actual source count, request
creation with that actual count, then exact custody transfer and native reads.
Production parents are not yet wired to this overload: source acquisition/failure
containment, worker-lifetime diagnostic adoption and terminal/persistence remain
the next integration work. No production activation or live-256 validation.

Verification: `artifacts/frontier-primary-source-count-20260919/RESULTS.md`.

## A8c15: exact original parent source preparation (implemented; callers disabled)

`FrontierParentCustody.TryPreparePrimarySource` consumes only the exact original
accepted completion, canonical source path and matching run/scenario. It accepts
the original parent's new attempt id and either supported parent role. Copied
custody/completions, foreign paths/contexts and invalid attempt/owner arguments
are rejected before a hash attempt; they cannot retire the original. A caller
that already owns the original hash may finish its existing operation, but the
new preparation cannot consume that hash or start another one.

Preparation performs the original native hash and A8c14 actual count, creates a
writer-bound request using that count and the exact completion, then transfers
the complete native input set. Count/request construction share the existing
cumulative 5000-ms parent work clock with native hash and transfer; it is never
reset. Failure after beginning closes partial request/custody resources. The
temporary hash lease is released on every path. No primary is started here.

The returned `FrontierPrimaryPreparation` retains the original request, input
capability and actual pre-analysis hash/count. Only the original wrapper can
expose or dispose those resources. Metadata is not an ownership substitute.
Original cleanup closes the request and all its native inputs; a copied wrapper
or preparation replay cannot retire a successful successor. The successor does
not depend on the old GUI request or temporary hash lease. All live/publication/
full-native-writer flags remain false. Input currentness is not terminal approval;
the original request remains owned until cleanup for later source/receipt gates.

The native headless chain covers both parent identities and extends preparation
through original worker/source/evaluation custody, diagnostic adoption, report
capture, terminal receipt and persistence. Its explicit test synchronization is
not production readiness. Production still uses legacy `TryAttach`, whose source
reader reopens a path. The next integration must connect native launch readiness
and `TryAttachFrontier`, arrange offer/adoption/commit without consuming adoption
too early, and preserve original parent reports/outcomes/cleanup before enabling
any caller. This checkpoint does not enable either parent or authorize live-256.

Verification: `artifacts/frontier-primary-preparation-20260919/RESULTS.md`.

## A8c16: original native launch readiness (implemented; callers disabled)

The original `FrontierPrimaryPreparation.StartWorkerWithReports` owns one launch
attempt. Copies, invalid redirects and already-started processes are rejected
before consuming it. It creates fresh manual-reset ready/failed rendezvous events
before starting the worker; an existing event is not reused or signalled. Setup
collision or a real process-start failure remains a failure, not a second launch.

The method composes A8c13's original report-preserving launch, actual worker
binding, native input handoff and optional diagnostic receiver preparation. Ready
is signalled only after successful native handoff and currentness revalidation.
Capture/receiver omission alone does not block the native source. Binding/handoff
failure signals failed while preserving the already-started worker's original
stdout/stderr tasks. Launch replay cannot revoke its original diagnostic output.
Root disposal wakes waiters and closes both owned events. No source pass is added.

`TryAttachFrontierReady` first validates the request id and actual parent process
incarnation, then waits outside the request mutex for ready, failed, cancellation
or parent exit, bounded by the existing 20000-ms producer deadline. Cancellation,
exit and failure win over ready. This wait consumes the same one-shot attachment
slot; failure cannot trigger a legacy/native attachment retry. Ready merely allows
the unchanged native attachment checks to run: exact bound worker, current parent,
context, source, native file/fact channels and pins remain necessary. A forged
ready signal without handoff is insufficient. No mutex is held while waiting, so
the parent can still bind its worker and prepare the actual handoff.

The internal `NLINK_RETAINED_FRONTIER_START_V1=1` selector is set on that child
launch. The analyzer uses the shared source-owner helper to select readiness-aware
native attachment; absent the marker, its legacy behavior remains unchanged.
Neither original production parent calls the new launch or source preparation.
The marker, readiness property and event states grant no authority, and all
existing live/publication/full-native-writer flags remain false.

Headless integration removes the fixture's source-begin dependency for this new
path and reaches original terminal persistence on both parent roles/hosts. Later
diagnostic offer/adoption/commit still uses existing explicit fixture coordination.
That remaining rendezvous, original-parent failure/receipt/persistence integration
and focused parent verification must precede any activation or live-256 test.

Verification: `artifacts/frontier-primary-start-20260919/RESULTS.md`.

## A8c17: native diagnostic offer/adoption rendezvous (implemented; callers disabled)

The existing diagnostic channel owns fresh manual-reset `.offered` and `.adopted`
events. Event-name collisions reject setup without changing the foreign event.
`Offer` and `Adopt` signal only after their existing exact state transitions and
after releasing the nonblocking channel mutex. This prevents a woken peer from
losing its one-shot operation merely because the signalling side still holds the
gate. Packet framing, states, private commit token and native validation are not
replaced or relaxed. Old explicit offer/adopt/commit methods retain their behavior.

The original receiver's `TryAdoptWhenOfferedAsync` reserves one wait and returns a
native task, so the parent can retain its original process watchdog and ordinary
report tasks. It does not consume `TryAdopt` until the offer signal arrives.
`TryCommitOutputWhenAdopted` likewise waits only after an original successful
offer, then invokes the existing commit checks against original evaluation/proof
state and exact adopted bytes. Neither wait holds an ownership or channel lock.
The original parent five-second and worker twenty-second active-work clocks are
not reset or charged for idle waiting. Each native wait is bounded by the existing
20000-ms producer limit; the caller's outer process deadline remains independent.

Request closure, actual held peer exit or diagnostic-channel cancellation wins
over a ready signal. Timeout or wait failure closes the existing optional custody;
no polling, retry, replacement worker, source read or authority evaluation is
added. Copied capabilities cannot consume the original wait. Repeated calls to
the new wait methods return false without revoking the original in-flight or
completed operation; legacy destructive replay behavior is unchanged. A successful
parent task means adoption only, not worker commit, exit, terminal approval or
transport ownership. Only the unchanged native `TakeAfterExit` and later gates can
issue those existing resources. All authority flags remain false.

The genuine headless chain uses A8c16 source readiness and these native diagnostic
waits through terminal persistence for both parent identities, without fixture
begin/adoption-release synchronization on the valid path. Deliberate fault cases
still use test-only gates to position mutations. Neither original production
parent nor the production analyzer calls the new wait APIs yet. Original-parent
source acquisition/failure containment, diagnostic scheduling and exact terminal/
persistence lifetime wiring remain prerequisites to any opt-in or live-256 run.

Verification: `artifacts/frontier-diagnostic-rendezvous-20260919/RESULTS.md`.

## A8c18: original compact analyzer integration (implemented; callers disabled)

Only an already attached native source enters the new compact branch. It binds
the original assembled full/compact arrays and source ordinals, records the
original two evaluation results, and captures native payloads from that exact
pair. The legacy writer/publication branch is unchanged for ordinary owners.
No new evaluator call, publication issuer or runtime authority is introduced.

Retain the array already materialized for first-failure selection and pass that
same array, its selected object, original recent-progress proof, index and views
to the native resolver. Do not pass the mutable candidate builder, clone the
failure, select a later failure, or reinterpret a diagnostic match as recovery.
After the original compact reports are written, the native witness may prepare,
offer and wait for adoption before unchanged commit validation. Omission is
optional; original reporting errors still propagate. Existing native cleanup
retires private resources; successful commit preserves the receiver's independent
escrow for the later actual-exit/output gates.

The production analyzer also exposed a prior path-reopen dependency: both its
direct bridge-startup correlator and the nested probe-sidecar classifier scan
raw source lines. When the native semantic read covers every source line in
order, those scans receive the already assembled complete lines. They still run
the original raw-evidence matcher with unchanged positions and fields. Incomplete
coverage cannot supply this cache; missing/foreign cache entries retain ordinary
path-read behavior. The cache is evidence only and carries no native capability.
It remains available if optional evaluation custody later declines, so diagnostic
failure cannot remove the original scan input. File sharing is not relaxed.

The genuine compact analyzer has three existing evaluator calls: its original
full/compact pair and a separate probe-sidecar subset evaluation. Earlier
two-evaluation headless custody fixtures did not cover that latter classifier.
A8c18 binds only the original pair and preserves all three existing calls; it
does not substitute the pair's views for the different subset evaluation.

This is not full-mode or original-parent activation. Neither production parent
uses A8c15/A8c16's native preparation/launch yet. Parent integration must schedule
adoption under the existing 1800-second analysis watchdog: A8c17's 20-second
offer wait must not be blindly started at launch and expire during normal
analysis. Original source acquisition, watchdog/failure containment, actual exit,
pipe EOF, exact terminal receipt and persistence lifetimes remain required before
enabling either caller. No live-256 test, portable rebuild or authority promotion.

Verification: `artifacts/frontier-analyzer-integration-20260919/RESULTS.md`.

## A8c19: original retained-analysis parents (implemented; live callers disabled)

Both original retained-analysis functions accept an internal optional
`FrontierParentCustody`. Only the explicitly enabled compact/source/report path
and typed original custody/completion may attempt A8c15 preparation. Native
preparation supplies the original pre-analysis hash/count passes, source request
and independent input owner; the legacy path is not additionally run on success.
Absent/declined preparation retains the old source path. Foreign objects do not
execute capability-shaped script methods. No production call site supplies this
new parameter or enables frontier report capture.

The prepared source starts the single actual worker through A8c16. The ordinary
stdout/stderr tasks, UTF-8 report writes, proof-artifact validators, timeout/exit
diagnostics, error messages and 1800-second watchdog remain the parent's original
operations. The native source root stays alive for the original post-analysis
receipt hash/count even if diagnostics are rejected or the worker fails/times out.

`TryAdoptIfOffered()` provides the missing watchdog scheduling seam. Its zero-time
native signal probe checks request/channel cancellation and actual held worker
exit; no offer returns false without starting a wait, consuming adoption, charging
or resetting the active-work stopwatch. A signalled offer invokes the unchanged
one-shot native adoption/packet checks. Copies, replay and an already started
asynchronous adoption cannot acquire or retire the original operation through
this method. A forged signal is not a valid packet. The original watchdog calls
this optional method only while its deadline has not expired, on its unchanged
5000-ms process wait cadence. The worker's original bounded commit wait remains.

After the actual worker exits and both ordinary reports drain, optional transport
take/output join may create terminal custody. The original receipt writer receives
that custody and the native source; its exact returned object alone is passed to
`Take`. Only a resulting native terminal object may persist the separate bounded
no-overwrite diagnostic, before normal cleanup. Neither persistence nor its
failure replaces the original receipt, proof verdict, throw or legacy publication
path. Timeout/nonzero exit cannot acquire the successful transport. All frontier
authority flags stay false.

This integration is exercised headlessly with the original parent functions,
genuine native input chain and actual compact analyzer, not a GUI/NKN run. It does
not enable full-mode native analysis, certify the original GUI-launch/finalizer
custody delivery, or certify all pre-launch optional rejection paths. Review those
upstream ownership/failure seams before any production opt-in; do not manufacture
custody from the legacy completion/relay or a saved JSON file.

Verification: `artifacts/frontier-retained-parent-integration-20260919/RESULTS.md`.

## A8c20: original parent failure containment (verified; callers disabled)

The original parents preserve A8c16's strict launch contract. An existing ready
or failed event, stale prepared native inputs, or a real process-start failure
does not trigger a fallback launch. These cases propagate their original error,
release prepared source resources, and create no worker reports or terminal
receipt for a worker that never started. A foreign readiness event remains
unsignalled and owned by its creator. No production launch API was changed.

Original preparation that exhausts its parent work budget retires its native
custody before returning null. Both parents then perform the ordinary legacy
pre-analysis hash/count and analysis, followed by their original post-analysis
passes and receipt. This does not generalize to foreign/copied argument rejection:
that rejection must not retire somebody else's original custody or release their
pins. No second native preparation/hash attempt or authority is created.

Output-capture and diagnostic-receiver collisions after successful preparation
still allow the original worker, native source read, three original evaluator
calls and ordinary reports/receipt. They omit the diagnostic result and do not
overwrite the colliding file/mapping. Existing Wrapper `proof_incomplete` and
RouteAcceptance `completed` classifications for this fixture are unchanged; the
latter remains report completion, not route correctness or frontier authority.

Headless tests extract both original parent functions and inject only the failed
resource at the pre-launch seam. They check cleanup, source-pass counts, ordinary
reports, terminal binding and absence of rejected diagnostics on both hosts.
All 28 failure cases plus four affected success regressions pass. This checkpoint
adds tests and contract clarification only; it is not GUI-parent integration or
comprehensive post-launch source-handoff failure certification. Upstream native
launch/ledger/app/finalizer ownership and parent delivery remain required before
any production activation or new live-256 run.

Verification: `artifacts/frontier-parent-failure-containment-20260919/RESULTS.md`.

## A8c21: original GUI launch readiness (implemented; live callers disabled)

The original GUI parent's one-shot early attachment previously depended on test
release events after `BindWorker`. Production wiring cannot rely on process-start
timing: an early failed attachment consumes the slot. `StartFrontierGuiWorkerWithReports`
now composes a single actual GUI launch, first ordinary stdout/stderr readers,
actual process binding, parent registration and a fresh ready/failed rendezvous.
Copies, relays, already-bound/retired requests, replay, invalid redirects and
already-associated processes cannot start a new GUI or consume a valid launch.
Replay cannot retire an earlier successful registration.

Readiness setup is optional at this earlier boundary: there is no prepared/pinned
primary source yet. A ready/failed event collision omits the native selector,
does not signal or reset a foreign event, and preserves ordinary startup/binding.
A binding/registration failure after launch signals failure and still returns
ordinary report tasks. An actual process-start error propagates without retry.
This does not alter A8c16's separate strict retained-source launch contract.

`TryAttachFrontierLauncherReady` consumes the same one-shot slot as the original
direct attachment. It validates the request ID and actual parent incarnation,
then waits outside the native mutex and active-work clocks for at most the
existing 20000-ms producer budget. Cancellation, parent exit and failure take
precedence over ready. Success only permits the unchanged GUI PID/incarnation,
registration bytes and context checks; signals are not custody or authority.
Timeout/rejection cannot retry through either entry point. The legacy finalizer
attachment slot remains separate.

The original hard-deadline parent accepts an internal `CaptureFrontierLaunch`
switch. Default callers retain the old launch/read branch and discard any inherited
`NLINK_GUI_FRONTIER_START_V1` selector. Explicit native launches set that child-only
selector after fresh signal creation. Reports, state persistence, deadlines,
timeout handling, actual exit and legacy completion/relay behavior are unchanged.
Readiness cleanup uses the existing launch lock so concurrent disposal cannot miss
signals created by an already-entered launch. The report wrapper remains private
to the parent; readiness and registration are not file custody.

No production call site enables this switch. At A8c21, the actual GUI entry still
needed to consume the ready-aware owner before original ledger/app startup. A8c22
supplies that entry/creation step below. Further integration must preserve
that owner through original cursor observations, app requests/output, finalizer
and parent file/fact/acknowledgment/commit delivery. The current GUI-parent helper
disposes its request before returning; a future integration must preserve or
transfer the exact native custody within its owning lifetime, not return a dead
capability or reconstruct it from legacy completion/relay metadata. No native
full-mode or live-256 activation follows from this launch boundary.

Verification: `artifacts/frontier-gui-start-20260919/RESULTS.md`.

## A8c22: original GUI entry and ledger creation (implemented; callers disabled)

The original GUI entry uses the exact `NLINK_GUI_FRONTIER_START_V1=1` selector
before `Clear-AppLogsIfPresent`, not at finalization. Other selector values do not
load native ownership types or consume the attachment slot. Malformed request IDs
are omitted; a real rejected scope consumes the original one-shot attachment and
cannot retry. The marker and environment remain locators, not authority.

The original append-only initializer optionally calls
`TryCreateLedgerOriginForGui(expectedPath, out sourceCreated)`. The expected path
must match the owner's fixed registered source; it cannot redirect creation.
This uses the same guarded original creation operation, read-only retained handle,
no-delete sharing and cumulative budget as `TryCreateLedgerOrigin`. The output
flag is reset on entry and set only after the genuine CREATE_NEW succeeds. It is
not accepted as an input and cannot supply origin/cursor/writer custody.

If native creation is rejected before it creates a file, the original ordinary
CREATE_NEW path runs. If it creates the file but later rejects optional custody,
ordinary initialization uses that already-created file without deleting,
recreating or claiming it as native ownership. Existing files still fail with the
original pre-existing-file error and are never overwritten. Missing acceptance
scope retains the ordinary rolling-log behavior. Legacy ledger IDs, generation
metadata, limits, append path, readers and cursor diagnostics remain unchanged;
native origin/observation IDs are not imported into these mutable objects.

The owner and origin remain private at the GUI entry and are passed explicitly
to creation. Both parameter scopes explicitly set the Private variable option;
assigning `$private:` to an already-bound PowerShell parameter does not do this.
The regression fixture checks formatter child scopes for leaked capabilities.
The original top-level finally closes the owner even if scenario cleanup throws.

This step does not wire app requests/output, native cursor synchronization or the
initial observation, finalizer processing, parent file/fact/acknowledgment/commit,
or transfer into retained analysis. In particular, ordinary cursor scans are not
native observations, and a creation handle alone does not make a run eligible.
All authority flags remain false. No original parent call site opts into native
GUI launch; no full-mode or live-256 activation is implied.

Verification: `artifacts/frontier-gui-ledger-entry-20260919/RESULTS.md`.

## A8c23: ordinary app reports across optional capture failure (callers disabled)

The older `StartAppWithOutput` can return null after starting an app. It remains
available for its existing bounded primitive callers, but that nullable result
must not select another GUI app launch. The new `StartAppWithReports` returns the
original ordinary report tasks after every successful start/report setup, even
when optional raw capture or native app binding fails. Its optional `Launch`
capability is separate from those tasks. It starts one process and one reader per
pipe, never a retry or a competing event reader.

The exact original app-request wrapper owns one output-launch attempt shared
with the older entrypoint. Copies, replay, already-bound requests, associated
processes, missing redirects or missing report setup cannot start another app.
A still-unused original request whose optional owner has expired/been retired
may perform the explicitly requested ordinary launch, without native capture or
binding. That is startup, not authority recovered from stale evidence.

A synchronous callback invokes the original PID-based ordinary report-file
creation. These paths are ordinary output destinations only; native raw files
still use the owner's fixed prefix and original handles. The line reader uses the
original pipe encoding, CR/LF/CRLF framing, blank lines and final unterminated tail,
and appends UTF-8 lines with the same newline policy as the existing GUI callbacks.
It retains only the current line, not an entire report. Native capture tees the
original raw bytes before decoding and retains its existing seal/bounds/EOF gates.

Optional native file creation, write, seal or binding failure omits diagnostics
while ordinary live reporting continues. Real process-start errors propagate.
Ordinary report setup errors also propagate; bounded-buffer discard readers then
drain the already-started app so a failed setup cannot leave its pipes blocked.
An ordinary report write error is retained, the pipe continues draining, the task
faults at EOF and optional native success is denied. Ownership disposal never
closes a running app's read pipes. Native exit success and pipe EOF remain separate.

`Start-AppInstance` accepts explicit private request/report-reference parameters;
the default event-based branch is unchanged. `Wait-GuiSmokeProcessOutputCaptureDrain`
can wait for the exact returned tasks within its existing deadline before its
unchanged quiet-file check. Task results are suppressed from the PowerShell output
pipeline. These report tasks are not imported native custody or authority.

No scenario calls these parameters yet. Original GUI entry-to-app request delivery,
app-side ready-aware attachment, native initial/cursor observations and finalizer/
parent custody scheduling remain unintegrated. No live caller, full-native mode,
route state, primary evaluation or completion verdict is enabled by this step.

Verification: `artifacts/frontier-app-reports-20260919/RESULTS.md`.

## A8c24: native app startup readiness and delivery (live callers disabled)

The A8c23 original report-preserving launch clears inherited app-start selector,
endpoint and request-id values in its child `ProcessStartInfo`, then supplies the
original request's locators only if optional capture and fresh native ready/failed
events are available. It does not modify the GUI process environment. A collision
with either named event omits the optional path without signalling/resetting the
foreign event, suppressing ordinary startup or launching a replacement app.

The original actual process is started once. PID-based ordinary reports and both
read tasks are established before native binding and ready publication. Binding
failure, setup failure and ownership disposal wake a waiting child through failure
or cancellation. Actual start/setup/report failures retain A8c23's error contract.
`NativeLaunchReady` is an output-only startup observation, not a current lease,
writer capability, payload receipt, successful exit or route/publication authority.

The shared Core/tools receiver requires the exact `NLINK_APP_FRONTIER_START_V1=1`
selector and a D-format request GUID. It checks native request identity/scope and
the GUI parent's actual PID/creation time, then waits at most 20000 ms outside the
request mutex and active-work clocks. Cancellation, held parent exit and failure
precede readiness and are rechecked after wakeup. Failure is checked again under
the attachment mutex. Ready only permits the original exact app PID/incarnation,
registration and stage checks; a forged signal cannot bind an app. Legacy direct
attachment and ready-aware attachment share the same original stage transition,
so neither can replay a successful attachment through the other entrypoint.

Preparation, ready publication, cancellation and signal disposal share a local
lock. Failed creation does not retain partially created signals, and disposal
cannot race preparation into leaked readiness handles. This is trusted-workflow
ownership, not a same-account security boundary. No wire stage/version, catalog,
writer, coordinator, source-read pass or evaluation rule changes here.

The receiver is compiled into Core but is not called by the real app entry yet.
Next connect the original lease to the app-owned catalog/writer lifecycle and
drain/file-handoff cleanup, then deliver the original requests/reports through the
actual two-app GUI scenario. Native cursor/finalizer and parent custody lifetime
integration are still required before enabling any live caller. No live-256 test
is required by this readiness-only checkpoint.

Verification: `artifacts/frontier-app-start-20260919/RESULTS.md`.

## A8c25: app file-ready and adoption rendezvous (live callers disabled)

App startup readiness is not file-channel readiness. The GUI can create channels
only after its two actual apps are bound and the launch set is sealed. Each native
app request therefore owns a fresh, initially unsignalled `.files-ready-v1` event
from request creation. Only original successful two-channel creation publishes
these wakeups, after the existing channel-ready events. A publication failure
retires the optional set; legacy channel/open/acknowledgment formats are unchanged.
Submission and adoption also publish their native state before waking the peer,
but the original callers now set their events only after releasing the channel
mutex and local submission/adoption lock. The earlier signal-under-mutex ordering
could wake a peer whose unchanged one-shot mutex check would fail. Two focused
regressions reproduce that ordering failure and pass after the change. Revocation
before the submission wakeup cancels the channel and releases its retained pins.

`AppLease.WaitForFileHandoffReady` is an internal, original-lease-only, one-shot
wait before catalog creation. It waits at most 20000 ms for readiness, request
cancellation or its held native GUI parent exit, with cancellation/exit taking
precedence and original currentness rechecked after wakeup. It holds no request,
catalog or logging lock and consumes no file-attachment attempt or active-work
budget. A failed original wait cancels that optional request. A copied or repeated
wait cannot consume or cancel the original's successful wait. A ready event alone
still supplies no channel: the existing original file attachment must open and
validate the exact channel/scope and consume its original attempt once.

After original submission, `FileHandoff.WaitForAdoption` provides a separate
one-shot wait of at most 20000 ms outside its ownership lock and native mutex.
Request cancellation, held GUI exit and channel/catalog cancellation precede
the adopted event. It rechecks original ownership, complete channel/submission
currentness and native Adopted state after wakeup; a failed original wait cancels
the channel. Copies/repeated waits do not consume the original acknowledgment.
Only the unchanged `TryTakeAcknowledgment` consumes that attempt and releases the
submission pins after its exact checks. Wait success never acknowledges files,
completes the app lease, grants publication authority or changes route/runtime
state. Old immediate attachment and acknowledgment retain their strict contracts.

These are readiness primitives, not activation of app-owned writer integration.
The existing GUI flow waits for the first app's window before starting the second;
file readiness must therefore run outside UI startup while ordinary startup stays
available. The next app owner must outlive service disposal, prepare exact markers
before the original final log drain and retain handoff custody until native
adoption/acknowledgment and lease completion. Parent/cursor/finalizer lifetime work
also remains. No live caller or new source/evaluation pass is enabled here.

Verification: `artifacts/frontier-file-rendezvous-20260919/RESULTS.md`.

## A8c26: app-owned background setup and final drain (live callers disabled)

The desktop Program owns one internal `FrontierAppLifetime`, selected only by
the existing exact `NLINK_APP_FRONTIER_START_V1=1` locator path. Benchmark/soak/
self-test/resource branches do not create it. Setup runs on a background task;
opening the first window does not wait for the second app or its file channel.
The worker attaches the original ready-aware lease, waits for file readiness,
creates its original catalog, attaches the channel and only then registers the
actual operational writer. No catalog/manifest/receipt is imported from a caller.

Program's original exit finally owns completion, after desktop/service disposal.
It signals a private setup-stop event and joins the setup task before finalizing
the writer. Both setup waits now optionally observe that internal stop handle;
all public readiness APIs retain their original deadline and exact checks. Stop
is rejection-only, checked again after wakeup, never a ready/ownership signal.

Finalization closes catalog admission, prepares remaining original observations
within existing limits, and takes either an exact positive catalog with markers
or the existing explicit zero-admission writer-only contribution. Failed or
incomplete preparation cannot become writer-only. The ordinary final log drain
runs once even when optional setup/preparation fails. Only a flushed original
contribution can submit native files, wait for adoption, take the original
acknowledgment and complete the app lease. Cleanup follows that sequence and
does not append a post-drain diagnostic. Copies/repeated calls cannot consume the
original owner's finalization or repeat its drain. The returned flush status and
internal handoff-completed observation are not live-proof or publication authority.

The owner is intentionally not owned by `SessionFileTransferService.Dispose`.
Real service observation admission is the next missing integration edge; current
headless tests attach existing deterministic producer observations to the private
catalog. They exercise actual preparation, writer receipts, native files and
acknowledgment in fresh app-role processes, not the live desktop/NKN scenario.
Actual GUI request delivery, cursor/finalizer and parent custody remain disabled
until that chain is complete. No route/runtime authority or evaluator changes.

Verification: `artifacts/frontier-app-lifetime-20260919/RESULTS.md`.

## A8c27: original service admission and capture closure (live callers disabled)

The desktop SessionRuntime uses `SessionFileTransferService.CreateForApp`, which
retains the exact original app lifetime created by Program. The ordinary public
service constructor remains ownerless. Factory binding does not require readiness
and does not block UI startup; actual admission still requires the original native
catalog, file handoff and current operational writer, before writer completion.
Copies cannot admit or clear the original process owner. Finalization clears the
factory's current owner without retiring already retained observations early.

There is one admission attempt per inbound transfer context, before its first
eligible sparse fallback repair write. The existing receive path supplies the
coordinator-issued current repair contract; the diagnostic predicate checks that
original reference, unsettled lifecycle, V6 post-Tuna fallback route, current
nonpaused context/data session and a chunk at the committed frontier. This is
scheduling evidence only, never transport or runtime authority. Ordinary and
nonmatching batches do not create a capture or retry a failed one.

The service reserves its diagnostic attempt under its gate, then releases that
gate before native checks and producer construction. The exact original catalog
creates the observation with that service's capture gate. Reattachment rechecks
the original context/contract, data-session reference and generation, and frontier.
A reset or intervening change closes the admitted observation as incomplete; it
does not replace it or recycle its catalog slot. Early unready admission rejects
the optional app capture rather than waiting in the receive path or reconstructing
missed timestamps later. Revoked/drained writers reject before new admission.

Capture still brackets the existing runtime UTC acquisition. It does not import
timestamps, source/receipt DTOs or observations. The original receiver/coordinator
callbacks supply repair, progress, frame, accepted completion and timing evidence.
All existing capture validation, 16-admission/byte/work bounds and authority flags
remain unchanged. No success-selection retry or new authority evaluation is added.

Before final preparation, the app closes each remaining service-owned capture
under its actual service gate, then releases all such gates before validation and
encoding. Closed positive observations keep their outcome across session reset
and service disposal. Open or invalid observations cannot become writer-only or
produce a partial positive catalog. No callback gains runtime authority from this
closure, admission, readiness or writer handoff.

Headless tests now use real service-driven admission instead of injecting an
observer. They still seed deterministic coordinator/transport fixtures and settle
the original emitter explicitly. Actual emitter/completion-writer readiness before
Program's final marker/drain sequence is the next prerequisite: stream completion
currently queues an asynchronous drain. Do not enable GUI callers or treat these
fixture results as an actual desktop shutdown or live-256 gate. Original GUI app
request/cursor/finalizer and parent custody integration remain after that seam.

Verification: `artifacts/frontier-service-admission-20260920/RESULTS.md`.

## A8c28: admitted-stream shutdown readiness (live callers disabled)

After closing and preparing the original catalog, the app lifetime waits for each
positive observation's original coordinator stream to submit its own final message.
The completion binding retains that emitter only when staging its original accepted
envelope. The real emitter worker signals after passing the exact final message to
the operational writer; an empty queue or another stream cannot provide readiness.
No synchronous forced drain or new coordinator evaluation is introduced.

The original completion receipt then waits for writer progress outside the logging
gate and revalidates its exact association, original receipt identity, native
registration and exact persisted batch. A later successful sequence cannot upgrade
a failed batch; copied, foreign, revoked and missing receipts still fail. Marker,
ordered completion/marker/drain and native handoff checks are unchanged.

All admitted observations share the timeout supplied by the app's existing final
call (five seconds), with remaining time charged across emitter and receipt waits.
This is additional to the existing ordinary drain and native adoption budgets,
not a claim that total shutdown is limited to five seconds. Waits release capture,
payload, catalog, binding, logging and native ownership gates. Any optional failure
still reaches the ordinary final writer drain; no partial catalog is submitted.

New headless native cases use actual service admission and production finalization
without fixture-only emitter Drain calls or final log-tail reads. They cover natural
scheduling, a held worker released after shutdown starts, and a held worker timing
out. Exact writer receipt tests cover pending, failed-after-later-success, copied,
foreign and revoked receipts. Existing deterministic coordinator fixtures are still
used; these results are not an actual desktop shutdown or live route-cycle proof.

Scope is the closed positive observation catalog. An empty writer-only catalog has
no emitter to wait for, and non-admitted/retired streams are not enrolled by this
change. Review that original producer-ownership boundary next, before enabling any
GUI caller or completing GUI request/cursor/finalizer and parent custody delivery.

Verification: `artifacts/frontier-shutdown-readiness-20260920/RESULTS.md`.

## A8c29: creator-owned retirement before all-producer readiness

The first broader-shutdown prerequisite is actual stream closure. Merely retaining
every stream would expose existing contexts that start a coordinator stream but
never become the installed inbound/outbound context. A diagnostic timeout or an
invented final message must not substitute for the creator's lifecycle cleanup.

Inbound offer processing now retains responsibility until the exact assignment to
`inboundTransfer`; outbound setup does so until assignment to `outboundTransfer`.
Their finally blocks release an uninstalled original context through existing
`DisposeResources`, outside the service gate. Busy/correlation rejection,
disposed/duplicate installation races and exceptional setup are covered. After
installation, the original service and its existing terminal/reset/disposal paths
retain ownership. Ordinary terminal replacement already completes its stream;
that behavior has not been changed. No new route decision, terminal verdict,
retry, publication, transport send or diagnostic authority is introduced.

Remaining bounded integration contract:

- Enroll original app-service streams before their first emission, not only
  when an observation becomes positive and not by inspecting current pointers
  at shutdown. Retired and never-installed contexts must remain represented.
- Retain bounded completion handles, not arbitrary context/resource graphs.
  Close enrollment once, share a finite readiness budget, reject overflow or
  incomplete sets, and never recycle an unsuccessful slot for a later success.
- Fence in-flight context creation against service disposal and finalization.
  A completion-handle snapshot alone cannot prove that another handler will not
  create an untracked stream afterward. No native checks/waits under service gates.
- Writer-only apps must satisfy the same producer-readiness gate. Then retain
  A8c28's exact completion/marker/drain checks for positive observations and all
  existing native source/finalizer/parent ownership checks. Readiness has no
  route/runtime authority and cannot manufacture a stream-completed event.
- Cover delayed retired, writer-only and rejected-setup emitters, creation versus
  sealing, timeout, capacity and cancellation in fresh headless native fixtures
  before any live opt-in. Ordinary drain must still run on optional rejection.

This checkpoint implements only the verified creator-retirement prerequisite,
not the broader registry/wait. Live callers remain disabled. Original GUI
request/cursor/finalizer and parent delivery remain later integration steps.
Verification: `artifacts/coordinator-retirement-20260920/RESULTS.md`.

## A8c30: bounded app-owned producer readiness (live callers disabled)

The original app lifetime now owns a diagnostic registry with lifetime limits of
16 service scopes and 64 coordinator stream completion tasks. Entries are never
recycled. The registry creates each original emitter and retains only its final
submission task, not the emitter, transfer context or its resource graph. It
accepts no caller task or replacement completion. Copied scopes cannot close an
original scope; copied registries cannot seal or invalidate the original.

The desktop service factory registers with the original app owner. Both real
inbound and outbound creation paths enroll before first emission, under the
service gate with a disposed check. Inbound construction/snapshot work remains
outside that gate. Disposal closes the scope under the same gate; A8c29 creator
cleanup and existing installed-context retirement still produce actual closure.
An in-flight enrolled context remains represented even after active pointers
are cleared. The ordinary public service constructor remains ownerless.

Finalization cancels/joins optional setup, requires every service scope closed,
then seals enrollment once and waits for all retained original tasks outside
service/registry/catalog/native gates. An open scope rejects immediately; this
diagnostic path does not dispose a service or invent its final stream message.
The stream wait and A8c28's exact positive completion-receipt waits share the
Program caller's five-second wait budget. Existing preparation, ordinary drain
and native adoption waits retain their separate bounds; this is not a new total
wall-clock shutdown deadline. Readiness never replaces exact persisted receipts,
markers, writer drain or native source/finalizer/parent custody.

Overflow, invalid enrollment, timeout or a late service invalidates the optional
handoff permanently. Ordinary emitters/transfers still operate on diagnostic
overflow, and ordinary final drain still runs. The original app owner remains
as a closed service-factory tombstone after `Current` is cleared. A late factory
therefore cannot silently become an ownerless service; it invalidates the
registry and cancels any original catalog/file handoff. Catalog validity includes
the original registry, preserving failure checks through native publication and
adoption. Registry code calls no native/catalog operations while holding its gate.

Eight focused registry checks cover closed/open scopes, copied owners, lifetime
capacity, late service/stream creation, a shared wait budget, and non-recovery
after late completion. Sixteen fresh two-app native cases across PowerShell 5.1
and 7 cover writer-only, delayed/retired, rejected/disposal-raced, timeout,
late-service and mixed positive/writer-only paths. They create real service
contexts and schedule the original worker; they do not force its drain or use
log-tail settlement as shutdown proof. Controlled delays/race callbacks are not
claims to cover every parallel interleaving or an actual desktop/live route run.
All 53 distinct focused/affected tests have passing latest results.

No coordinator authority, route policy, acceptance verdict or performance setting
changes. Next integrate original GUI request/cursor/finalizer and native parent
custody delivery. Live callers remain disabled; no live-256 test is required yet.
Verification: `artifacts/frontier-producer-registry-20260920/RESULTS.md`.

## A8c31: original scenario launch and initial cursor (launch-only, live disabled)

The scenario has an explicit optional original-owner parameter. Its live caller
does not supply it, and no new environment selector enables that caller. The
branch creates exactly two requests from that owner, passes them through the
existing helpee/helper flows into the original report-preserving app launcher,
and seals the resulting original pair. Failed request creation omits optional
ownership without launching twice or changing ordinary startup decisions.

After helpee startup and the existing ordinary bookmark/snapshot checks, but
before helper launch, the owner selects its own ledger origin and captures its
initial native observation. This operation accepts no caller origin, bookmark,
file or success flag. Existing cursor limits, one-shot checks and revocation
apply. Native and ordinary bookmarks may differ if more bytes arrive between
their reads; neither is overwritten or presented as the other's evidence. Later
native processing must use the original native observation and exact source.

Owner, requests and report references are private PowerShell inputs/locals,
explicitly forwarded only where needed. They are not added to scenario context,
formatters, result JSON or ambient environment. Ordinary started process objects
remain in the scenario's existing cleanup list. The native launch callback adds
its actual process immediately after startup, before creating ordinary reports;
a report-setup exception therefore keeps cleanup ownership and propagates the
original error. There is no replacement launch, fabricated report task or retry.
This fixes the native branch's demonstrated first-child setup leak; it does not
refactor the separate legacy launcher or claim to fix unrelated legacy failures.

The original report tasks reach the existing finalizer's bounded output-drain
operation, including after optional cancellation. The branch is deliberately
launch-only: it does not enable file channels and cancels optional setup before
ordinary cleanup. The current cleanup requests window closure, then waits for
process exit; enabling file handoff now would let apps wait for adoption while
the GUI waits for those same apps to exit. The next integration must schedule
original file adoption before those exit waits, within existing shared budgets
and failure cleanup, without polling consuming operations before submissions.
It must not extend the current seven-second ordinary cleanup wait or treat a
forced exit as writer/attachment success; expiry must retire optional ownership.

Headless native tests execute the full original scenario function and role flows
with only UI/network boundaries stubbed, plus the original finalizer cleanup/drain
block. They verify real process/request delivery, initial native bytes versus
unchanged ordinary scalar bookmarks, single launches, cancellation, cursor
failure, copied ownership, capacity rejection, report/window setup failures and
ordinary report draining on both PowerShell hosts. They do not exercise actual
windows, NKN transfers, file adoption, final-source sealing or parent custody.

No route/runtime authority, primary evaluation/verdict change or live activation
is introduced. Next integrate native adoption-before-exit, then original
cursor/finalizer/parent delivery. No live-256 test is required yet.
Verification: `artifacts/frontier-scenario-launch-20260920/RESULTS.md`.

## A8c32: native adoption before process-exit waits (live entry disconnected)

The explicit scenario branch now enables file channels only after sealing its
original pair of app launches. Its original owner is explicitly forwarded through
private finalizer/callback/cleanup inputs, not inherited by child scopes or added
to scenario context. Scenario cleanup retains that owner until the existing
finalizer returns, including when finalization throws. The live entry still
passes no owner; no new selector activates it.

After requesting closure of both app windows, cleanup invokes one native
`WaitForFileAdoptions` operation with its remaining wait budget. The owner chooses
its own two requests, channels, submission events, cancellation events and native
process handles. No caller source, task, event, request, completion scalar or
adoption callback is imported. Repeated/copy waits do not consume or replace an
original wait; an original zero-budget, absent-channel or mixed manual-adoption
attempt fails closed.

Only an exact submitted event plus submitted state allows the existing original
one-shot adoption operation. Either app may submit first. Once adopted, its
submission/exit handles are removed from the pending wait set, so its successful
exit cannot block or falsely reject the other app. At most eight native handles
are observed per wait, and no GUI/adopter/channel lock is held while idle.
An early submitted signal without the original submitted state rejects the set
without consuming that slot. Cancellation, premature pending-app exit or timeout
also rejects it. The wait never manufactures acknowledgment or attachment success.

The existing cleanup stopwatch covers adoption plus both subsequent process-exit
waits, retaining the seven-second default and old one-millisecond expired-budget
clamp. A temporary native deadline also participates in original pull/hash/control
scope checks, alongside the unchanged cumulative five-second native work budget.
Failure retires ownership before removing that deadline. This is cooperative
validation around native operations, not a hard interruption of a blocked OS I/O
call or a new total wall-clock bound for window-close/forced-cleanup operations.

After exit waits, deadline expiry or absent original file acknowledgment,
attachment completion or successful process exit discards the optional owner
before any forced termination. Partial successful app contributions do not make
the whole set valid. Ordinary stop/bridge cleanup and report-task draining remain
in their existing order; default no-owner action sequences remain unchanged.

Fresh native tests run the actual Core app lifetime, producer tracking, writer
drain, original native submissions/adoption/acknowledgment and actual process
exit/report tasks. Only the window-close request is replaced by a fixture event.
They cover writer-only and positive contributions, second-app-first adoption,
delayed original stream completion, app rejection, cancellation, shared-budget
expiry, copied/repeated calls, zero budget and premature submission signals on
both PowerShell hosts. They do not prove desktop window closure, NKN routes,
final-source sealing, native finalizer custody or parent delivery.

Next bind the adopted files and original final cursor/source processing to the
same finalizer and native parent lifetime. The legacy finalizer's scalar outputs
cannot supply that ownership. No route/runtime authority, verdict/evaluation
count, performance policy or live activation changes. No live-256 test is required
yet. Verification: `artifacts/frontier-cleanup-adoption-20260920/RESULTS.md`.

## A8c33: original finalizer source preparation (live entry disconnected)

The actual `Write-FileTransferGuiLogLedgerEvidence` now prepares the optional
native source only when explicitly given the original scenario owner. Its
existing cleanup/output-drain block runs first. A missing original issuer or a
reported drain failure prevents preparation; the existing file-seal operation
independently requires both native acknowledgments, attachment completion,
successful app exits and original output EOF.

The finalizer then takes its matching original sealed-file binding and calls
`TryProcessObservedLedger` once. That existing operation owns final-cursor capture,
source reading, recovery markers, writer/cursor/recovery joins and retained
temporary outputs under the unchanged cumulative native work budget. No caller
bookmark, frozen line count, source path or success flag is imported from the
legacy snapshot. The semantic event list remains the existing bounded input.

Binding and result are private finalizer locals, not ambient child-scope values,
scenario context, pipeline output or `LocalNativeWriterCapture`. The separate
legacy processing/evidence path remains unchanged; native temporary results do
not replace its paths or certify its results. This is an optional native source
preparation alongside ordinary finalization, not another authority evaluation.
Failure discards optional ownership and leaves ordinary finalization able to
continue. The outer finally releases native result/binding resources even when
legacy finalization throws. Existing legacy completion can retire that ownership
earlier; preparation does not extend the original finalizer lifetime.

Native integration fixtures reuse actual Core app-lifetime children and native
cleanup/adoption. They execute the original full finalizer function, inspect
prepared native ownership at its legacy-sync boundary, then deliberately fail
legacy snapshot capture to test exception preservation and outer-finally cleanup.
They do not validate the later legacy persistence body with native preparation
active. Separate compatibility tests cover unchanged ordinary finalization.

Next integrate the original semantic-copy/evidence-persistence gates and native
parent delivery before legacy completion. Do not append those operations after
completion or substitute existing legacy output files for original native
capabilities. Live entry remains disconnected; no native custody/publication or
live-256 claim follows from source preparation.
Verification: `artifacts/frontier-finalizer-source-20260920/RESULTS.md`.

## A8c34: separate native semantic copy and persistence (live entry disconnected)

The actual finalizer's explicit optional branch now follows successful original
source processing with `TryCopySemanticEvidence` and `TryPersistEvidence`, before
ordinary finalization. These existing one-shot gates retain their original
cursor/copy/persistence ownership checks and cumulative native work budget. The
persisted-evidence object is another private finalizer local, never a callback
input or a substitute for the public legacy capture. Optional failure retires
native ownership while ordinary finalization continues. The outer finally
disposes the native evidence, result and binding; original completion or
cancellation may revoke them earlier.

### Destination isolation

The old native copy destination collided with ordinary finalization's
`filetransfer-retained-log-slice.log`. The native copy now always chooses the
distinct fixed `filetransfer-frontier-retained-log-slice.log` under its original
artifact root. There is no caller-selected destination, overwrite fallback,
alternate path search or import of an existing legacy copy. The native evidence
record remains `filetransfer-frontier-finalizer-evidence.json` and derives its
analyzer-role path from the original copy capability. Existing legacy filenames,
evidence schema and completion payload are unchanged.

### Current catalog and bounded selection

The actual shared semantic catalog currently has 1138 names, totaling 56098
UTF-16 code units (longest name 91). A8c33's fixture-selected subset did not expose
that the real catalog exceeds its 1024-name native bound. The native binding and
stream processor now share one `MaximumSemanticEventNames = 2048` constant.
No names are truncated, filtered heuristically or silently omitted. The combined
selector/deferred-key ceiling remains 65536 code units; each entry remains at
most 1024 code units; deferred keys remain capped at 64. Oversized count or
aggregate metadata is rejected before reading source bytes. The original work
budget and 64-witness cap are unchanged, and no authority evaluation is added.

### Integration verification boundary

Native closure must observe acknowledgment after attachment completion or process
exit, not reuse a channel state sampled before those observations. The app
publishes ACK before completing its attachment and exiting. Reusing the earlier
state could retire valid ownership during either transition. The closure check
now follows that causal read order without a retry loop, new wait or relaxed
ACK/exit/cancellation requirement. A deterministic model extracts the current
method and compares it with the pre-fix cached reads; it is not native custody
proof. An intermittent native cleanup rejection motivated the review, but its
exact process interleaving was not captured.

The subsequent native trace identified a separate post-wait gap: adoption and
ACKs were complete, but ordinary `Process.HasExited` had caused cleanup to skip
a process wait before the original kernel process handle was signaled. The
native exit requirement was correctly stricter than that observation. Cleanup
now calls one original-owner `WaitForAppExitSignals` operation before the
unchanged ACK/attachment/successful-exit checks. It uses the remaining original
cleanup budget for both handles, waits outside ownership locks, and includes
original cancellation/channel/parent-exit signals. It does not poll, renew a
budget, change exit-code requirements or turn HasExited into native authority.
Timeout or invalid ownership retires the optional path. A repeat cannot renew
the wait. A controlled early-HasExited fixture uses an actual still-running app
and requires its native exit before persistence; separate extracted-method wait
tests check lock availability, cancellation, timeout, missing adoption and repeat.

New headless-native cases use original ordinary append-only ledger creation and
sync, the real semantic catalog, real Core app-lifetime children, native file
handoff and the whole original finalizer's ordinary copy/persistence body. They
check normal legacy completion alongside native persistence, native copy/evidence
collisions, unchanged legacy collision failure, cancellation and legacy exception
cleanup. Desktop window closure remains simulated and no live NKN routes run.
Lower-level checks retain exact bytes/hash/origin and no-overwrite coverage.

Persisted diagnostic JSON alone cannot recreate native ownership after teardown.
Next connect original native parent delivery while the GUI still owns its files,
before legacy completion and GUI exit. Do not consume parent pull/adoption
operations before their original offers exist or renew work/watchdog budgets.
Live activation remains disabled; no live-256 claim follows from this checkpoint.
Verification: `artifacts/frontier-finalizer-persistence-20260920/RESULTS.md`.

## A8c35: exact native analyzer/legacy-label join (live entry disconnected)

2026-09-20. The current-tree A8b35 positive parent-commit regression fails after
A8c34 because the native analyzer creation path is now deliberately separate from
the legacy decoder's fixed analyzer label. This is the first failed proof edge
found during the parent-scheduling review, not a new live-network failure. The
older A8c34 tests did not exercise this later completion/parent join.

`LocalFrontierParentFacts.MatchesCompletion` now requires both exact paths under
the original accepted completion's artifact root:

| Field | Required fixed basename | Use |
| --- | --- | --- |
| Original native facts, output 3 | `filetransfer-frontier-retained-log-slice.log` | Immutable original creation-path metadata |
| Unchanged legacy completion, output 3 | `filetransfer-retained-log-slice.log` | Logical analyzer label only |

Comparison remains exact; alternate roots, names, case spellings and dot segments
are not normalized into acceptance. Matching basenames or hashes alone do not
suffice. Original immutable ledger/bookmark/writer/content checks remain intact.
The native descriptor packet and exact read-only handles still bind file identity
and length, including the analyzer copy; the legacy codec still carries no copy
identity. Nothing is reopened using either path during this join.

No changes are needed to the primary transport: the existing parent hash reads
native pin 4 while assigning the legacy label to its verified lease. The primary
request and worker retain that exact label/scope and separately validate the same
native file identity. The worker's decoded original fact packet applies the same
two-path join. Native bytes, line origins, witness selection and evaluation count
are unchanged. Locked conflicting legacy-file tests cover the hash/count, worker,
terminal receipt and diagnostic persistence path for both parent-owner labels.

The downstream regression run exposed additional stale filesystem assumptions
from the former shared filename. Terminal hash/count helpers returned empty/-1
before using their original owned reader when the legacy-labelled file was absent.
The real retained-analysis parents rejected that absent label before attempting
native preparation. Ordinary analyzer resolution also omitted absent labels before
native attachment, and parsing checked file existence before its owned-reader
branch. These are input-delivery checks, not evidence that the native file is lost.

The corrected path checks remain on ordinary file-based branches. Only successful
original native preparation bypasses a parent's legacy-file preflight. Only an
actual attached request and acquired reader can supply the analyzer's logical
source label; a native-start environment flag or raw path alone cannot add parser
input. Parsing still requires the exact owned reader, matching single-source path
and zero-tail mode. Terminal helpers still validate original reader type/readability
and incompatible arguments, and reads retain native scope/currentness guards.
No source hash/count pass, content copy, metadata file or authority evaluation is
added to satisfy a missing legacy path.

Completion publication, successful native GUI exit, original one-shot post-exit
take, completed output capture and persisted state remain required. No scalar
path, signal, JSON or copied receipt can activate custody. GUI five-second active
work, parent five-second cumulative work, the 250-ms completion tail, native
handle caps and all runtime authority flags remain unchanged.

### Remaining integration seams reviewed with A8c35

1. Original rendezvous: parent registration exists before GUI readiness, but file,
   fact, ACK and commit channels are created later. Current consuming methods are
   one-shot and reject early calls. Add owner-bound readiness notifications/probes
   before scheduling them; do not retry the consuming methods. Signals are only
   scheduling evidence, and the original methods still validate every transition.
   Preserve the original process watchdog and cumulative work clocks, with idle
   waits outside ownership locks and no fresh timeout for each step. Cover missing,
   early, stale and cancelled signals on both hosts before wiring the live caller.
2. One completion boundary: `TryCompleteParentHandoff` already calls the original
   finalizer's `Complete`, which retires GUI native ownership. The real finalizer
   also has an ordinary `Complete` call after legacy evidence persistence. Native
   integration must choose one completion path at that boundary, not complete twice
   or append handoff after retirement. Earlier optional failure must retain ordinary
   finalization; completion failure must not be retried as a new native issuer.
3. Parent lifetime: the original watchdog currently takes completion after its
   successful exit/output/state gates, then disposes the original request in its
   `finally`. Before enabling the branch, consume native custody into the exact
   original primary request within that lifetime. Returning a raw custody reference
   or relaying legacy completion cannot extend/reconstruct native ownership. Keep
   the original primary invocation and evaluation count; do not run another primary
   merely to consume the new input.

These scheduling/call-site changes are not implemented by the filename-join fix.
Live entry remains disconnected. No portable rebuild, GUI/NKN run, live-256 test,
commit or push belongs to A8c35. Verification and the retained red reproduction:
`artifacts/frontier-parent-handoff-20260920/RESULTS.md`.

## A8c36: original GUI/parent handoff rendezvous (live entry disconnected)

2026-09-20. The original parent registration creates six bounded manual-reset
notifications before registration readiness: files offered/pulled, facts offered,
acknowledgment offered/confirmed, and commit prepared. Their names are derived
only from the original registration endpoint. The GUI opens that exact set at
attachment; neither peer imports public event handles or caller-selected names.
Creation collisions fail closed and close local handles without resetting or
cancelling the preexisting event. The 128-byte registration envelope and all
file/fact/ACK/commit and legacy completion codecs remain unchanged. This optional
registration now requires both peers to include the rendezvous implementation;
it does not fall back to unscheduled consumption for an older peer.

`FrontierFinalizerBinding.TryPrepareParentHandoffWhenReady()` is parameterless and
one-shot. It schedules the original attachment, native preparation/export, fact
publication and ACK confirmation methods. Between those original operations it
waits for the exact parent notifications outside ownership locks and active-work
clocks. One cumulative 20-second wall deadline covers the entire rendezvous; it
is never renewed per stage. Original launch closure, native parent exit, registration
cancellation and provisional-file cancellation abort waits. Currentness is checked
before and after each wait, and the original methods validate the actual complete
proof chain. Failure or repetition retires optional GUI ownership; a copied
binding cannot consume or retire the original.

`FrontierParentRegistration.TryPrepareCommitIfOffered()` is a nonblocking watchdog
probe on the exact original registration. Absent notifications neither consume
the original file/fact/ACK/commit one-shot gates nor charge parent active work.
When offered, it invokes only the existing operations and emits readiness after
each succeeds. The existing cumulative five-second parent and GUI active-work
budgets are unchanged. A completed probe returns its original provisional custody
once; later probes return null without retiring that issued custody. Failure
retires the optional registration and any provisional escrow. Copied registrations
cannot consume or retire originals. Callers must not interleave direct one-shot
calls with this orchestration path.

Notifications are scheduling evidence, never file receipt, ACK, commit, runtime,
route or publication authority. In particular, even a forged commit-prepared
notification cannot substitute for the original commit channel. GUI preparation
does **not** call `Complete` or retire successful GUI ownership. The original
caller must invoke `TryCompleteParentHandoff` once at its sole completion boundary;
that unchanged method checks/arms the original commit and calls the original
finalizer's `Complete`. The unchanged 250-ms completion budget, successful native
GUI exit, output/state gates and original post-exit take remain mandatory.

Headless native-process verification and retained results are recorded in
`artifacts/frontier-parent-rendezvous-20260920/RESULTS.md`. The positive fixture
uses these readiness operations without test-event handshakes between handoff
stages, then transfers accepted custody to the exact original primary request
before retiring the GUI request. This is not a live caller integration or an
additional primary authority evaluation. All live/publication/custody authority
flags remain false. Focused cases also prove prompt cancellation of a pending
lock-free wait by GUI repetition, and ordinary completion after earlier optional
rendezvous failure; no native completion is retried as an ordinary issuer.
The real finalizer's single completion-path selection and
wrapper's in-lifetime primary consumption remain the next integration seams;
raw custody must not escape request disposal. No live-256 test is warranted yet.

## A8c37: real finalizer's single completion boundary (live entry disconnected)

2026-09-20. `Write-FileTransferGuiLogLedgerEvidence` now selects between native
handoff completion and ordinary completion at its existing sole issuer call site.
The existing ordinary integrity/writer validation, verified copy, atomic JSON
persistence and finalization-stage update all precede the choice. No native offer
is scheduled before those gates pass. The unchanged original native binding and
its private evidence remain the only inputs to A8c36's readiness preparation;
legacy JSON or formatter output is not imported as native authority.

The preparation result stays private to the real finalizer. If no original binding
exists, it is stale, or earlier optional preparation fails, the original ordinary
`Complete` path remains available. If preparation succeeds, the finalizer invokes
only `TryCompleteParentHandoff`, which already calls the original `Complete`.
A failed or throwing native completion is not retried through ordinary completion.
The public capture stays provisional, and the existing outer finally retires GUI
ownership on success or failure. No new timeout, native codec, primary evaluation,
route/runtime authority or publication flag is introduced.

Current-tree reproduction before the change returned an ordinary completion but
no parent native custody. The headless integration fixture runs the whole original
finalizer with actual Core app-lifetime children and the original native GUI and
parent capabilities. The parent services readiness without inter-stage fixture
handshakes. Successful cases accept custody only after actual GUI exit and the
original take, then prepare the exact original primary source inside GUI-request
lifetime. This transfers ownership without launching an additional primary.
Failure cases verify ordinary fallback before native completion, no fallback after
a failed native completion, and no offer after a persistence-stage failure.

The old fixture assumption that GUI disposal makes native files writable is no
longer valid after an actual parent commit. GUI cleanup retires its own handles;
the parent's read-only escrow must still prevent writes. Tests now check that
boundary and require write access only after parent/primary disposal.

This is the GUI completion edge, not finished live parent integration. The real
wrapper watchdog still needs to service the A8c36 probe. Its primary analysis runs
after the watchdog returns and disposes the GUI request, so raw custody cannot be
returned to that later call. A bounded, exact ownership transfer must bridge those
existing call sites without moving/repeating the primary evaluation or weakening
the original successful-exit/output/state gates. Live callers remain disconnected;
no portable build, GUI/NKN run, live-256 test, commit or push belongs to this step.
Evidence: `artifacts/frontier-finalizer-handoff-20260920/RESULTS.md`.

## A8c38: private wrapper-to-primary lifetime handoff (live entry disconnected)

2026-09-20. The original wrapper GUI watchdog can now service A8c36's nonblocking
probe when explicitly given `CaptureFrontierLaunch` and empty in-process output
references for both original completion and prepared source, with no relay. It
keeps the original raw custody private. Readiness is serviced within the existing
watchdog loop; time is sampled after bounded offered work. Existing scenario,
finalization and primary timeouts are unchanged.

Only after actual successful GUI exit, complete original stdout/stderr capture,
ordinary state persistence and the original completion take may custody prepare
the primary source. Exact original completion, fixed retained-source label,
run/scenario and Wrapper ownership bind that preparation. Original native pins
are independently transferred before the GUI request's finally closes raw
custody. No primary process or authority evaluation starts in this watchdog.
Unexported preparation and raw custody are disposed on failure; existing output
owners are never overwritten. The public watchdog result remains exit evidence
only, and all native authority/publication flags remain false.
Because PowerShell typed references may silently convert assigned objects, the
export also verifies exact reference identity after assignment. A converted value
is cleared and the original preparation remains locally owned for disposal.

The later `Invoke-TunaGuiRetainedAnalysis` accepts this original preparation only
with the existing native capture opt-ins and live-proof mode. A bounded one-shot
claim checks original wrapper/request identity, unbound request currentness,
exact original completion, owner/run/scenario/path and native input currentness.
Copied, stale, foreign, repeated or conflicting explicitly supplied resources
reject before source reads or worker launch, without consuming a foreign owner.
When no preparation is supplied, existing ordinary and direct-custody paths are
unchanged. A valid claim preserves the prepared request's original attempt ID,
source passes and worker/report/receipt cleanup path; it does not create a second
request or move/repeat analysis.

The headless integration uses the whole real watchdog, real finalizer and actual
Core app-lifetime children, then the existing real retained-analysis consumer and
analyzer. It includes an intervening ordinary-log update after GUI-request cleanup
to distinguish pinned native input from pathname reopening. These genuine GUI
fixtures do not synthesize the unresolved grace failure required for a frontier
diagnostic: absence of that diagnostic does not relax route proof or turn the
ordinary `proof_incomplete` result into success.

Input isolation is not proof of live input equivalence. The existing wrapper's
`Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice` can add missing route-selected
and live-epoch-started/recovered observations and chronologically rewrite the
ordinary retained file after GUI exit; the finalizer explicitly labels that input
`mayBeMilestoneMerged`. Before connecting a live caller, establish how the original
primary's post-merge input and exact native source origins remain compatible,
without importing observations as authority, dropping required proof evidence or
adding a second evaluation. A8c38 does not claim to resolve that boundary.

Live entry calls still do not pass the opt-ins or these resources. The separate
RouteAcceptance process cannot acquire native ownership from the legacy scalar
relay. Connecting live call sites, defining any cross-process custody bridge and
diagnostic/correctness acceptance remain separate work. No live-256 run, portable
build, commit or push is enabled by this change.
Evidence: `artifacts/frontier-wrapper-handoff-20260920/RESULTS.md`.

## A8c39: post-merge input compatibility (live entry disconnected)

2026-09-20. Fresh headless A8c38 captures have identical ordinary/native semantic
bytes before merging. A nonempty real milestone merge changes the ordinary input
and can reorder its source ordinals. Keeping the pre-merge preparation would make
the later primary miss those observations; pathname equality and derived JSON do
not establish native source identity. Do not change the primary's input to make
the optional native diagnostic available.

The bounded compatibility policy is exact-byte admission, otherwise optional
omission. `Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice` accepts an internal
private reference to original preparation. Before any merge work it reserves that
original unclaimed Wrapper preparation for the exact run/scenario/path. Copies,
foreign scope and repeated reservations fail without consuming the original.
The existing merge algorithm and chronological sorting are unchanged.

The derived receipt's existing input hash pass can now use
`HashAndHoldAnalysisInput`, not a second hash/scan. Its real ordinary-file read
lease remains private in the original preparation. The original line-count pass
and atomic JSON persistence stay in place. Only successful merge/receipt
completion, unchanged original reference, exact captured SHA-256/byte count,
current native request/inputs and a still-held ordinary input permit completion
of this binding. The pin prevents write/delete/replacement until primary cleanup.
Pending binding cannot claim the original analysis or directly start its prepared
native worker. Hash and binding completion are one-shot; repeated/foreign calls
do not retire another owner.

For absent, ignored or already-present milestones, exact input retains native
ownership and original physical origins. If bytes change (including encoding-only
changes), the optional native preparation is disposed and its original reference
cleared. The later existing consumer takes its unchanged ordinary source path,
including all added/reordered milestone lines, and runs once with the same three
evaluations. No line is assigned a native origin from an old ordinal. Missing
receipt prerequisites also omit native ownership. Merge/hash/receipt exceptions
retain their original failure behavior and release native and ordinary pins;
a replaced output reference is preserved, not overwritten or disposed.

This is a conservative compatibility gate, not a derived-native-source projector.
Changed input has **no native frontier diagnostic** through this handoff. It is
still analyzed by the original primary, and its failures are not recovered or
upgraded. Retaining native diagnostics for transformed inputs would need a
separately reviewed exact-origin contract, not scalar receipt import, a guessed
mapping, a second authority evaluation or the removal of milestone observations.

Default merge/receipt callers remain unchanged, and live entry calls still do not
enable the native opt-ins. RouteAcceptance's separate-process ownership remains
the next integration boundary. No timeout, coordinator, route, transport, witness
budget, portable build, live GUI/NKN/256 test, commit or push changed in this step.
Evidence: `artifacts/frontier-milestone-compatibility-20260920/RESULTS.md`.

## A8c40: RouteAcceptance relay boundary review (native transfer not implemented)

2026-09-20. Review of the real call sites identifies a missing native-resource hop,
not an analyzer failure. `Invoke-RouteAcceptanceChildScriptNoThrow` owns the Wrapper
process, starts its first stdout/stderr readers, waits for exit, collects both
streams and forwards output before taking the optional relay. Only afterward does
`Invoke-TunaRouteAcceptanceRun` call the original RouteAcceptance primary. The
Wrapper independently owns its GUI child and may forward the GUI completion before
its later summary/integrity gates fail. Successful GUI exit is therefore not
sufficient to accept anything in RouteAcceptance.

`LocalGuiWriterRelay` transfers a bounded completion record with separate Wrapper
and GUI process identities. It does not transfer native handles or original parent
custody, and `TakeRelayAfterExit` deliberately does not mark that GUI completion as
freshly accepted in RouteAcceptance. A legacy read lease plus this completion may
still enable the existing ordinary source/publication request; it must not create
`FrontierPrimaryInputs`. The native GUI launch and parent-registration APIs reject
relay-mode requests. Do not remove those role guards or reinterpret a relayed
completion, its JSON, a source hash or a pathname as native ownership.

The earlier direct `FrontierParentCustody` RouteAcceptance fixtures test a primary
owner name in the original GUI-parent process. They do not exercise this additional
Wrapper-to-RouteAcceptance process boundary. A8c38's prepared Wrapper request also
cannot cross that boundary: it is an original in-process resource tied to its
creator and primary owner. Serializing it or starting analysis in the Wrapper
would not implement the required handoff.

The reviewed integration target is the RouteAcceptance-owned regular activation
cycle only. In that workflow the Wrapper skips its Wrapper-owned milestone merge
and primary; RouteAcceptance does not add that merge. Preserve this existing input
behavior. The measured-fallback log-selection path changes the source and is not
part of this target. A8c39 remains the compatibility policy for the separate
Wrapper-owned path, not evidence that a RouteAcceptance input has been checked.

The next bounded implementation is an opt-in, one-hop native escrow, initially
disconnected from both live callers and primary consumption:

1. Bind a distinct original relay registration to the existing RouteAcceptance
   request, run/scenario/root and exact Wrapper process incarnation. Registration
   alone grants no native file ownership. Preserve the legacy relay and native GUI
   roles, collision rejection and original first output readers.
2. Only the Wrapper's original accepted GUI custody may offer its held read-only
   files and immutable native facts. Pull/acknowledge those resources while the
   Wrapper still owns them, before its GUI-request cleanup releases raw custody.
   Reuse bounded native descriptor/fact validation where applicable; a descriptor
   or matching scalar record alone cannot mint an offer. Rebase handle numbers
   only against the pinned Wrapper process, preserving historical GUI/app origins.
3. RouteAcceptance holds provisional independent read-only pins, not an accepted
   primary source. Service an actual offer during the existing child wait without
   an evaluator, early primary launch, renewed scenario deadline or second output
   reader. Specify and test finite native wait/active-work budgets before wiring
   that service. Failed/partial offers, cancellation, wrong incarnation, replay
   and peer exit release all provisional resources without altering ordinary exit.
4. Final adoption requires the original successful Wrapper exit, complete original
   output handling, original one-shot relay take, matching exact completion body
   and current escrow. Wrapper nonzero exit or output failure must revoke even a
   fully pulled native offer. Only exact accepted resources may later detach into
   a parent-owned lifetime before relay-request disposal; no copied/reference-
   converted output can escape cleanup or overwrite an existing owner.

Primary consumption is a later integration gate: preserve the original
RouteAcceptance owner, invocation timing, source passes, three evaluations,
1800-second analysis budget and terminal receipt. Prove exact compatibility with
the original ordinary input, retaining it when native ownership is omitted; do not
silently substitute the native source or import old physical ordinals. That gate
is not implemented or proved by this review. No resource-registration milestone
may set native-writer, live-proof, publication, route or correctness authority.

Focused headless tests exercise the actual relay launcher on both PowerShell hosts
and check native-role rejection before startup and after binding, non-adoption of
relayed metadata, absence of reconstructed native inputs, and unchanged original
exit/output/workflow behavior. They use the existing controlled GUI completion
fixture, not a new native relay, real GUI/network validation or end-to-end native
custody proof.

The isolated output-failure cases exposed a prerequisite error-handling defect:
`Process.Start` does not set PowerShell's `LASTEXITCODE`. Reading that variable in
the relay catch either threw under strict mode when unset (skipping the subsequent
relay cleanup) or substituted a stale unrelated native status. The bounded fix
uses failure code 1 for relay exceptions and consults an optional native exit
variable only on the original direct-native-command path. It does not change a
normally observed Wrapper exit, suppress the original output error, mutate the
parent's native exit state or permit relay adoption after output failure. Tests
require disposal of request mappings, synchronization handles and the held child
process handle on success and failure. This catch repair is the only A8c40
production change; the native transfer remains unimplemented. No portable build,
live-256 test, commit or push is enabled. Evidence:
`artifacts/frontier-route-relay-contract-20260920/RESULTS.md`.

## A8c41: original one-hop relay registration (no file escrow or live wiring)

2026-09-20. The first A8c40 edge is implemented as a separate, internal opt-in
registration. `TryRegisterFrontierRelay` requires the original relay-mode request
already bound to its actual live Wrapper child. The ordinary GUI request role,
unbound request, same-process child, copied request and sealed request cannot
register. Request ID, exact run/scenario/root bytes, both process identities and
creation times, reserved fields and pre-forward legacy state are bound to the
original request. No caller-supplied descriptor, completion or digest is accepted.

The original attached Wrapper `Relay` may call `TryAttachFrontier` once. It derives
its scope from that original relay and pinned parent process, not API arguments.
The private fixed-size channel contains only registration identity, a fresh nonce
and a monotonic attachment observation. Complete header/body/padding comparison
rejects changed scope and channel bytes. Its mapping is 16,480 bytes; register and
attach use nonblocking mutex acquisition and each checks the existing 250 ms
completion-work bound. This step introduces no readiness wait, polling loop,
scenario/analysis timeout change or output reader.

The returned `FrontierRelayRegistration` and `FrontierRelayHandoff` are original
in-process wrappers without public constructors or writable properties. Here
"handoff" means the registered peer only: neither object holds evidence files or
provides a source reader. Copied wrappers are inert, including disposal. Repeated
register/attach calls return no new object without retiring the successful
original. A copied legacy `Relay` can no longer forward or dispose its original
owner; legitimate original legacy forwarding remains unchanged.

Failed opens and name collisions close only locally opened resources and never
signal a foreign cancellation event or overwrite a foreign mapping. Explicit
native disposal cancels only registration; the ordinary relay remains usable.
Request take/disposal and original Relay disposal close their native registration.
Peer exit, scope mutation and observed state regression invalidate it; beginning
ordinary forwarding ends the pre-forward scope. Native currentness checks retire
invalid registration resources rather than rehabilitating a restored snapshot.
This is local trusted-workflow ownership, not a security boundary against another
process running as the same account.

All native-writer, live-proof and publication flags remain false. No completion is
manufactured by registration. The existing actual GUI completion can still pass
through the ordinary scalar relay after native registration or its explicit
disposal, with separate Wrapper/GUI identities and original successful-exit take.

Headless tests use actual parent/Wrapper processes on PowerShell 5.1 and 7, with
controlled synchronization only in the fixture. They cover original/copy/replay
roles, tampering, collisions, cancellation, abrupt Wrapper exit, legacy forwarding,
unchanged output and complete named-resource cleanup. Legacy-forward cases also
use the existing whole Wrapper watchdog and actual controlled GUI child. These
are not native file-transfer or live network tests.

There are still no production callers of the new opt-ins. A8c40's held-file offer,
provisional read-only pin transfer, successful-Wrapper terminal adoption and
ordinary-input compatibility are subsequent edges. The next bounded step is the
original accepted GUI-custody offer and provisional pull over this registration;
do not import scalar metadata as custody or move the primary into the Wrapper.
No portable build, live GUI/NKN/256 test, commit or push is enabled. Evidence:
`artifacts/frontier-relay-registration-20260920/RESULTS.md`.

## A8c42: original relay escrow, terminal adoption and compatible primary handoff

2026-09-20. Extends A8c41's registration, without weakening its original-peer or
role checks. The Wrapper's original accepted GUI custody may now offer independent
read-only duplicates, the bounded existing descriptor/facts packets and its exact
encoded completion. A distinct fixed-capacity transfer mapping binds the original
registration snapshot (including nonce and process incarnations). Its immutable
header, lengths, digest and payload are validated before native descriptor pulls;
handle numbers are rebased only against the parent's held actual Wrapper process.
Map/event collisions close local resources without modifying foreign objects.

The one-hop states are offered, pulled and forwarded. Pull validates the immutable
facts against the original descriptor origins and completion, then duplicates the
actual read-only file objects. Neither state nor a packet is a custody issuer.
Only an original accepted GUI custody can offer; scalar relays, copied owners,
repeated operations and caller-authored facts cannot enter this API. Eligibility
consumes the one-shot offer before resource publication, including failed offers;
foreign arguments do not consume it. All three
native-writer/live-proof/publication flags remain false.

Registration/readiness is established after the sole actual Wrapper start and its
first stdout/stderr readers. A 20-second idle readiness wait precedes the child's
unchanged exact attachment checks. During the existing synchronous Wrapper wait,
the parent services a nonblocking offer probe at 50 ms exit-wait intervals. The
Wrapper may wait at most 20 seconds for its offered pins to be pulled. GUI-parent
offer work shares its existing cumulative five-second budget; parent pull and
native forwarding validation have five-second active-work bounds. These waits do
not start an evaluator, renew a scenario deadline or create second output readers.

Original legacy forwarding commits the exact offered completion body to the
native channel only after its ordinary forward succeeds. Independent parent pins
remain provisional until successful actual Wrapper exit, completed original
output handling, the original one-shot relay take and byte-exact completion/body
and scope matching. Nonzero exit, output failure, cancellation, malformed offers
or failed adoption release provisional pins. Successful detach is one-shot and
requires the exact original relayed completion; reference conversion cannot export
ownership. Detach precedes request cleanup. Already queued cancellation/exit
callbacks cannot revoke a detached owner by consulting the retired request.

RouteAcceptance's original ordinary hash and line-count passes run before native
primary preparation. Their verified held lease must match the accepted native
source's SHA, byte count, line count and exact logical path. This is compatibility,
not an authority factory: original accepted native custody supplies all native
pins and origins. No extra source scan or evaluation is added. An independent pin
on the ordinary input stays alive through analysis cleanup. A mismatch omits the
native preparation and preserves the original ordinary input; native physical
origins are not attached to changed bytes. The primary remains RouteAcceptance-
owned, after Wrapper exit/output, with one launch, three original evaluations,
the original 1800-second budget, original receipt and unchanged correctness gates.

The real callers use this path only under `CaptureLocalPublicationDiagnostics`,
`live-regular-activation-cycle` and `RegularActivationCycle`, with an available
empty local output reference. Other route modes and measured-fallback source
replacement remain unchanged. The Wrapper does not merge milestones or run an
early primary in this workflow. Optional transfer failure preserves the scalar
relay and ordinary analysis path. This work changes no coordinator or transport
runtime behavior, route-cycle verdict, retry cadence or throughput setting.

Headless verification uses actual parent/Wrapper/GUI/app/primary processes,
whole original launcher/watchdog/analysis functions and existing native finalizer
and analyzer fixtures. The report records final test results and build readiness:
`artifacts/frontier-relay-transfer-20260920/RESULTS.md`. Headless receipt completion
is not a live route-cycle pass. The next live-256 scenario still must prove a
second coordinator-published `file_tuna_v4` route. No live-256 run is authorized by
this checkpoint itself.

## A8c: original primary evaluation custody and terminal acceptance (integration later)

The primary chooses witnesses for its original reported failure from its original
completed full/compact evaluations. A native-owned payload can become a candidate
only after A8b custody validation; exact unique source/repair/recent/root/successor/
write/frame/completion links still apply. Preserve native physical origins, the
unchanged 64-witness budget, relevant contradictions, original views and existing
evaluation count. No additional authority evaluation or nearest-success fallback.

Use a distinct frontier-diagnostic output capability, not the existing publication
completion issuer. Bind original primary custody, invocation identities, native
input descriptors, exact selection and payload bytes. Recheck ownership during
preparation and final publication; cancellation, source revocation, partial output,
wrong process/request, repeated take and replay must omit diagnostics. The parent
must keep its original exit/output/source/terminal gates and consume once after
the expected worker exits. It cannot reconstruct authority from returned JSON.

Initially this output remains diagnostic-only, with no primary failure/count,
Recovered/Superseded, route or acceptance changes. Correctness approval is a
separate subsequent contract; no old or historical run is retroactively upgraded.

## Verification order

A8a uses fresh service fixtures for both write paths, multiple successors and the
actual pre-recheck inter-gate race; it must prepare before stream closure, preserve
clock samples and mappings, reject non-positive producers, exclude private values,
reject copied/foreign preparation ownership, and enforce strict UTF-8 limits.

A8b then needs fresh native app/writer/finalizer tests for exact bytes and process
roles, old-channel compatibility, marker ordering/drain, mutation/partial output,
capacity, cancellation after preparation, cleanup and export failure containment.
A8c needs native primary/parent tests on PowerShell 5.1 and 7, including revocation,
replay, unchanged primary views/verdict/counts and zero extra evaluations. Only
after those gates should correctness approval and a new targeted live-256 run be
considered. No portable rebuild, live test, commit or push follows from A8a alone.
