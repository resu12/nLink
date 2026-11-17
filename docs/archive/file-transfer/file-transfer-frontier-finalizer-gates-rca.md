# Frontier finalizer gates: current-state review and bounded fix sequence

Date: 2026-09-16. Base HEAD: `2691fc70f98d668bae943ebd41c6f8540156ec57`.
This is an inspection of the current accumulated working tree, not an RCA for a
new live failure. Production integration remains disabled.

## Finding

A8b20 retains the original processor output bytes, but the original public
`GuiSmokeLedgerStreamingResult` exposes mutable writer records, drain arrays,
deferred-batch records and counters. Its existing stream capture froze only the
combined processor-error flag. Retained files therefore did not make the remaining
in-memory validation inputs immutable. A later consumer could observe different
facts after the result was returned.

There is also no basis for treating a false processor-error flag as successful
writer closure: a writer can have contiguous sequences and valid chronology but
no drain, multiple drains, or a drain before its final sequence. The production
finalizer checks these separately in `Write-FileTransferGuiLogLedgerEvidence`.

## Gate inventory

| Gate | Current proof | Remaining requirement |
| --- | --- | --- |
| Original GUI launch readiness | A8c21 supplies a single report-preserving GUI launch, exact parent registration and bounded ready-aware attachment; A8c22 consumes it at actual GUI entry and owns source creation; original parent integration is default-off | Wire apps/output, original native cursor observations, finalizer and native custody lifetime; ready/creation are not full file custody or authority |
| Ordinary app reports | A8c23 preserves live line files and one reader per pipe across optional raw capture/binding failure; A8c24 adds child-only request delivery; A8c26 consumes it in a default-off app lifetime and A8c27 binds service-driven observation admission | Review writer-only/non-admitted stream readiness, deliver exact requests/reports through the scenario and retain original cursor/finalizer gates; report tasks/readiness alone grant no authority |
| App file-channel/adoption readiness | A8c25 supplies bounded native waits; A8c26 owns final preparation/drain and handoff; A8c27 closes actual service capture gates; A8c28 waits for admitted positive streams and their exact completion receipts; A8c29 retires uninstalled contexts; A8c30 enrolls all original app-service streams before emission and fences creation/shutdown | GUI request/cursor/finalizer and native parent delivery remain unintegrated, with no live authority from headless success |
| Original source and three outputs | A8b16–A8b20 retain original native objects and exact line origins | Preserve ownership through all later gates |
| Processor and writer-closure facts | A8b21 captures bounded immutable facts; A8b22 joins their exact original snapshot to both adopted native writers and terminal drain sequences | Preserve the exact original match through later gates; this is not full finalizer approval |
| Recovery markers | A8b23 retains all four original pipe-capture files; A8b24 parses exact original readers, derives bounded batch keys and joins unique scoped recovery tuples to immutable ledger facts | Preserve the exact original recovery match through later gates; it is not publication approval |
| Initial/final ledger and cursor integrity | A8b25–A8b26 own source creation/indexing/observations; A8b27 joins exact LF byte indices, bookmark/count and immutable writer-prefix/final facts to the same original processor invocation | Preserve the exact current cursor match through copy/persistence and later custody; it is not publication approval |
| Semantic analyzer copy | A8b28 copies from the retained original semantic handle, compares actual bytes/physical lines/SHA-256 and retains the original destination as read-only | Preserve the exact current copy through persistence and later custody; a pathname or equal metadata is not that operation |
| Evidence persistence | A8b29 serializes original validated facts into a separate bounded frontier JSON record, flushes and renames its exact native file, then retains read-only custody | Preserve the exact current persistence capability and original inputs through parent/primary handoff; legacy JSON is unchanged and is not upgraded |
| Native set preparation | A8b31 prepares the complete original GUI file set as bounded read-only local pins, retaining exact original facts/origin maps by reference | Preserve pins and exact facts through A8b32 and the later fact-transfer/acknowledgment gates; preparation is not custody |
| Provisional parent native pull | A8b32 publishes a bounded native descriptor packet and pulls the entire read-only set through the parent's original held GUI process handle | A8b35 preserves the exact set in separate committed custody; retain its mapping through the later primary handoff |
| Provisional parent facts | A8b33 transfers the complete bounded immutable fact packet, joins it to the exact native offer and checks cursor/origin/writer/range/recovery consistency | A8b35 retains these private facts with parent custody; later primary input must consume that exact owner, not reconstruct it |
| Two-sided acknowledgment | A8b34 joins the exact original parent receipt and GUI binding through an immutable offer/fact-bound envelope | A8b35 consumes it through a separate completion/exit-bound escrow; confirmation alone does not release GUI resources |
| Parent custody across GUI exit | A8b35 retains an independent bounded read-only set and activates it only through exact original completion, native successful exit and private fact agreement | A8c1 consumes it into original primary-request custody; no public reader or full-custody/publication flag |
| Primary-request native inputs | A8c1 retains exact native/fact ownership, performs the original optional source hash and owns private independent-position readers | A8c2 transfers the exact set to the original native worker; parent readers cannot run after an offer |
| Native primary-worker attachment | A8c2 binds request/process scope, rebased parent handles and immutable full facts, then imports the complete read-only set through the actual parent handle | A8c3 consumes the native source; input/view association and publication remain separate |
| Native primary source | A8c3 maps the original semantic-copy handle, verifies bounded EOF and binds exact emitted strings to immutable physical origins | A8c4 joins supplied original arrays and views; source provenance alone is not evaluation authority |
| Native primary array/view association | A8c4 pins exact arrays, maps their emitted references to physical origins, records full then compact views and issues one exact invocation pair | Private-primary primitive only; trusted production seam, bounded failure capture, native descriptors and diagnostic output remain later gates |
| Evaluation-bound native payloads | A8c5 captures exact held payload bytes once, verifies original manifest hashes and strict UTF-8, and retains an original pair-owned set | Payload provenance only; native receipt/failure/completion joins, 64-witness selection and diagnostic output remain absent |
| Native physical witness batch | A8c6 captures 1–64 original full-array positions with exact source/compact/byte origins and joins the chosen payload's native completion/marker ranges | A8c7 supplies the trusted-primary semantic diagnostic join; no publication capability |
| Native receipt/failure diagnostic join | A8c7 joins the original unresolved failure, native receipt links and original full/compact completion evidence within the same witness/work limits, with a bounded native-held relevant-field snapshot | A8c8 adds original-state revalidation and local preparation; mutable result wrappers remain non-authoritative |
| Original-state/local diagnostic preparation | A8c8 rechecks bounded original note-property/array observations and binds one exact local preparation to original native input descriptors, invocation pair, payload and witnesses | A8c9 rechecks the same original guard at final transport commit; mutable wrappers remain diagnostic-only |
| Separate native diagnostic transport | A8c9 retains independent parent escrow, adopts exact bound bytes while the worker is alive, rechecks original state at commit and takes transport once after successful native worker exit | A8c10 joins original pipe-output ownership; transport does not publish or approve reports |
| Original primary pipe output | A8c10 starts the exact unstarted primary, owns both first reads and joins successful native EOF/capture to the original diagnostic transport | Original receipt writing, source/result agreement and terminal-report acceptance remain separate |
| Original terminal receipt | A8c11 binds native output/transport/source/catalog to the original writer's atomic write and exact returned receipt | Diagnostic-only status; original request lifetime still applies |
| Terminal diagnostic persistence | A8c12 saves the original packet and native-bound terminal metadata before cleanup, with one-shot exact native file custody | Historical diagnostic only; no production parent activation or portable authority |
| Ordinary primary report preservation | A8c13 returns report tasks independently of optional capture/binding success; A8c19 composes those tasks with original parent reporting; A8c20 verifies parent capture/receiver collisions | No live caller opts in; upstream custody delivery remains |
| Original pre-analysis source count | A8c14 counts the original native hash lease once with strict decoding and independent position; A8c19 uses that pass in both original parents | No metadata-derived count or authority; retain the original source for both post-analysis receipt passes |
| Original parent source preparation | A8c15 composes exact custody/completion, original hash/count, request creation and native transfer; A8c19 invokes it inside both original parents; A8c20 verifies budget rejection and ordinary legacy continuation | Live callers remain disabled; foreign rejection must not retire an original owner's pins |
| Original native launch readiness | A8c16 binds the actual worker, prepares native handoff and optional receiver, then wakes the worker outside the request mutex; A8c20 verifies original parent collision/stale-input/start rejection and cleanup | Unchanged native attachment is mandatory; pre-launch rejection is not permission to restart |
| Diagnostic offer/adoption readiness | A8c17 supplies post-mutex native wakeups and bounded waits; A8c19 adds a non-consuming offered-signal probe in the original watchdog | Original packet/custody/commit/exit gates remain mandatory; no launch-time short offer deadline |
| Original compact analyzer | A8c18 binds the existing full/compact evaluation pair, original selected-candidate array and native payload; complete original lines supply both existing raw-evidence scans without path reopening | No additional evaluator call or changed failure/report; native full-mode and live callers remain disabled |
| Original retained-analysis parents | A8c19 composes exact source preparation, single native launch, watchdog-driven adoption and exact original receipt/persistence; A8c20 verifies selected preparation/launch/capture failures in both parent functions | Original GUI-parent custody delivery and upstream failure containment still need review before any live opt-in |
| Completion and parent custody | A8b30 registers the original parent and attaches the original persisted GUI chain through a separate bounded control record; legacy completion remains unchanged | Complete separate native pull/adoption/acknowledgment/cleanup before legacy completion retires the optional set and before GUI exit; registration is not custody |

## Implemented first edge: A8b21

The original stream capture now privately owns a validation snapshot created before
the processor returns. It records original counters, immutable writer scope and
sequence facts, each writer's drain count/single-drain sequence, and immutable
deferred-batch tuples/hashes. It checks reported writer closure without asserting
native membership, recovery-marker validation, cursor integrity, copy or persistence.

The snapshot is capped at 64 writers, 64 deferred batches, 1024 code units per
captured text field and 65536 aggregate text code units. No drain array is retained
or cloned: count plus the unique sequence are the exact facts needed by the
existing drain rule. Read-only collections contain newly constructed immutable
records, not aliases to the mutable result records. Copies, foreign snapshots,
stale source/output and original-owner retirement cannot establish currentness.

Bounds failure omits the snapshot. The GUI helper requires the exact current
snapshot and otherwise retires the optional result/source/output set. Data errors
remain immutable diagnostic facts; they do not become full finalizer approval.
No second source pass, output hash pass or authority evaluation is added.

## Exact native writer join: A8b22

The original finalizer binding now consumes only its exact original typed result,
source reader, stream capture and immutable validation snapshot. Both reported
writers must be closed without processor errors, match the two original adopted
writer IDs ordinally, and end at their exact native drain sequences. The join is
one-shot, requires full source EOF and runs before the original cumulative
processing interval finishes. Copies and foreign results cannot consume the
original attempt. An original failed/repeated/late join retires the optional set.
An omitted over-bound snapshot is an original failed join, not a foreign candidate;
it also retires the source, outputs and payloads immediately.

The same processor source is compiled into an isolated namespace in the optional
ownership assembly, preserving the legacy standalone processor and completion
format while allowing a compile-time typed join. There is no caller-supplied
success flag, public scalar authority import, second processor invocation, source
read, hash pass, new native handle or separate budget. The exact match remains
current only while its original source, outputs and finalizer binding do.

## Implemented recovery edge: A8b24

Original marker capture precedes ledger processing under the same cumulative work
interval. Strict UTF-8 parsing, bounded original line origins and exact app/writer
membership establish the marker snapshot; duplicate fields, terminal failures,
missing/orphan/duplicate recovery pairs and mismatched tuples fail closed. The
helper no longer accepts caller-supplied deferred-batch keys. Its original typed
result must match both A8b22 writer closure and the exact selected immutable
batch set. Copied objects cannot acquire or retire original ownership; failures,
late/repeated joins and original retirement close the optional set. Mutable public
result facts are not used for this comparison. No production caller is enabled.

## Original semantic-copy operation: A8b28

The remaining copy gap was operation ownership, not hash availability: the legacy
finalizer reopens a semantic pathname and checks mutable scalar results. The new
optional binding operation takes no parameters, requires the original current
cursor/writer/recovery matches and finished processing, and reads only the private
retained semantic handle. It uses the same cumulative five-second adopter clock
with 32-KiB blocks and checks currentness before/after I/O and final retention.
No fresh allowance, processor/source rerun or authority evaluation is added.

The binding derives `filetransfer-retained-log-slice.log` under its registered
root and opens it with `CreateNew`, never overwriting existing evidence. The copy
hashes the bytes written and counts LF boundaries using the legacy physical-line
rule, including empty output and no final newline. Byte count, line count, exact
immutable semantic-origin count and SHA-256 must agree with the original capture.
The output byte bound allows only the existing source cap plus bounded newline
expansion. Flush and original native identity capture precede a reduced read-only
duplicate; write/delete exclusion and cancellation ownership are retained.

This is creation/operation custody, not atomic path publication. A failed partial
destination is closed but left as diagnostic bytes without a copy capability; it
is not deleted or adopted by pathname. The legacy atomic-copy helper is unchanged.
No production path-only consumer is enabled. Only the exact original current
`FrontierSemanticCopy` validates the copy; copied capabilities/metadata cannot
acquire or retire it. Failure, repetition, closed inputs/destination, expiry,
cancellation, cursor disposal and legacy completion retire the optional set.
All live-proof, full-native-custody and publication flags remain false.

## Original frontier evidence persistence: A8b29

The legacy JSON is mutable and written by a path-based finalizer. Importing that
JSON or a success flag would not own its validation or persistence. The optional
binding now has a parameterless, one-shot `TryPersistEvidence` operation. It
requires its exact current copy and therefore the original cursor/writer/recovery
joins, completed processing and sealed source. It generates a distinct
`filetransfer-frontier-finalizer-evidence.json`; it neither replaces nor attests
to persistence of the legacy `filetransfer-gui-log-ledger.json`.

The fixed schema captures original run/scenario/ledger/generation identities,
sealed source hash and counts/bookmark, initial/final cursor observations, all
four original output identities/hashes/counts/paths, validated writer sequences
and recovery counts. It is a diagnostic summary, not a serialization of the full
origin maps, recovery tuples, private payloads, or a route/acceptance verdict.
Those original inputs remain retained and required by the live capability; later
parent custody cannot be reconstructed from this JSON.

Strict UTF-8 encoding is limited to 64 KiB, including JSON escaping; individual
strings are capped at 4096 code units. Numbers are culture-invariant. Serialization,
hashing, 32-KiB writes, flush, rename and retention share the existing cumulative
five-second adopter budget. The original processing cancellation token is reused
only after original EOF/completed work, without a new lifetime or allowance.

A create-new temporary native file excludes external write/delete access. After
`Flush(true)`, the same open file is renamed by handle with replacement disabled,
then duplicated with reduced read-only access before its writable handle closes.
There is no close/reopen custody gap or pathname rediscovery. The native operation
uses the documented [FILE_RENAME_INFO no-replacement contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info).
This proves successful local flush/rename, not power-loss durability of filesystem
directory metadata or publication/acknowledgment to another process.

Only the exact current `FrontierPersistedEvidence` reports `EvidencePersisted`.
Copies cannot acquire/retire it. Missing gates, repetition, encoding/size limits,
collision, I/O failure, stale inputs/output, expired budget, cancellation and
legacy completion omit or revoke it and close the optional set. A failed temporary
or already-renamed file can remain as diagnostic bytes, but never supplies a
successful capability. No existing file is overwritten or deleted.

## Original parent registration: A8b30

The first remaining parent edge was an original receiver, not more scalar
completion metadata. The parent now registers once through its exact original
`LocalGuiWriterRequest`, after `BindWorker` retained the actual GUI process handle.
Unbound, self, relay, copied, exited or retired requests cannot register. Copied
requests also cannot bind, take completion or dispose the original request's
resources. The legacy completion encoding and successful original path are unchanged.

A separate 128-byte, versioned control record binds the original request GUID,
fresh registration nonce, parent/GUI PIDs and creation times, and SHA-256 of the
exact original request body. Reserved bytes must remain zero. Its only transition
is registered to attached; unknown or regressed state fails closed. It carries no
file handles, paths, origin maps, payload bytes, verdicts or acknowledgment.
Opening a foreign/rejected registration only closes that local open; it cannot
cancel the original record. Attachment uses a nonblocking native mutex.
GUI attachment is charged to the existing cumulative five-second adopter work
clock; its lock-free scope callback also observes parent-registration cancellation.
It creates no fresh work allowance, file read/hash pass or authority evaluation.

The GUI's parameterless `TryAttachParentHandoff` requires its exact original
persisted-evidence capability, preserving the copy/cursor/writer/recovery/source
chain. Repetition, altered control or legacy scope, cancellation, closed evidence,
original retirement and legacy completion revoke the optional attachment and its
resources. Parent and GUI copies cannot consume or retire original capabilities.
`GuiAttached` reports peer registration only, not evidence validation by the
parent, adoption, acknowledgment or permission to retire the GUI resources.

No file object crosses the process boundary in this checkpoint. Therefore GUI
exit still invalidates the registration; it does not leave surviving parent
custody. All live-proof/full-custody/publication flags remain false. Production
integration, primary evaluation and verdict/count behavior are unchanged.

## Complete native preparation: A8b31

A8b30 identified the original receiver, but each original GUI resource still held
its file separately. A scalar list of paths/hashes or reconstructed JSON would
not supply the complete original native set for a parent pull. The first missing
edge is therefore all-or-nothing preparation, before a descriptor is published or
a parent duplicates any GUI handle.

The parameterless, one-shot `TryPrepareParentFiles` requires the exact original
parent attachment and persisted-evidence chain. It pins one sealed raw source,
the three processor outputs, semantic copy, persisted evidence, four original
stdout/stderr captures, and every private payload from both original native
writer contributions. Ten mandatory files plus at most sixteen payloads per
writer give a hard maximum of 42 files. Original source/output/payload byte bounds
are preserved, including empty semantic/pipe files where already permitted.

Each pin duplicates the retained native object with reduced read-only access,
checks disk-file type, native identity and exact length, and rejects duplicate
roles, file identities or payload observations. Seal requires all ten roles and
the exact original payload objects in their original request slots. Payload
descriptors are not regenerated from mutable result fields. Neither source nor
output/payload contents are read or rehashed, and no pathname is reopened.

The preparation capability retains the exact original stream capture, cursor,
recovery markers, evidence and parent attachment by reference. The capture keeps
the immutable origin maps and validation snapshot; the native set keeps both
original writer manifests. This is not a serialization of those facts or their
transfer to the parent. All work uses the existing cumulative five-second clock
and original processing cancellation token. Resource callbacks take only resource
locks; they do not re-enter GUI/finalizer ownership locks.

Copies cannot acquire or retire originals. Missing/stale inputs, repeated
preparation, partial pin/shape failure, budget expiry, cancellation, closed pins,
evidence/attachment disposal and legacy completion fail closed and release the
whole optional set. The separately retained GUI files and maps cannot be retired
on the strength of preparation. No handle/manifest export, parent acknowledgment,
primary custody, authority evaluation or production integration is enabled.

## Implemented provisional native pull: A8b32

The first missing cross-process edge after preparation was the original parent
retaining the actual native files. The new parameterless one-shot offer/pull APIs
use a separate fixed 2376-byte mapping bound to the A8b30 registration snapshot.
A strict 2248-byte packet retains role, original writer-request slot and payload
observation mappings for ten mandatory files plus at most 32 payloads. It has no
paths, imported public-result facts or diagnostic-JSON reconstruction.

The parent duplicates through its original held GUI process handle, requesting
read-only access and checking file type/native identity/exact length. Partial
failure closes all locally owned duplicates. State 2 records a complete native
pull, not acknowledgment. Native GUI exit, request closure and channel cancellation
proactively close pending pins through resource-only callbacks. Neither a copied
capability nor repeated/late/stale operation can transfer or retain authority.
GUI publication shares the original work/token; parent native pull is one bounded
five-second operation, with no per-file reset or GUI budget extension.

This deliberately does not recreate full finalizer facts in the parent. Original
origin maps, cursor indices, validation/writer/recovery facts and all GUI pins
remain owned in the GUI. Parent files have no reader and still revoke on GUI
retirement; acknowledgment and independent post-exit custody remain absent.
Legacy completion, production behavior and primary evaluation counts are unchanged.
Verification: `artifacts/frontier-parent-files-20260916/RESULTS.md`.

## Implemented original-fact transfer: A8b33

The parent previously held native pins without the GUI's original maps and facts.
The new separate fact channel carries those bounded immutable inputs, not the
small diagnostic JSON or a reconstructed original capability. It binds the exact
native offer snapshot and preserves full physical origins, line-start indices,
cursor observations, frozen validation/prefix/writer/batch facts, original writer
manifests, recovery tuples/positions/keys and copy/persistence metadata. Receipt
checks their structural agreement and joins against the parent's pending native
identities, lengths, original slots and payload observations.

The bound is 4 MiB, including at most 3 MiB of cursor/origin indices. Encoding and
strict UTF-8 decode use fixed aggregate limits and one attempt. GUI work remains
inside the original budget/token; parent native pull and fact receipt share one
cumulative five-second clock. An initial unused-capacity rescan inside nested
currentness checks exhausted the GUI budget at 5006 ms. Padding is now checked at
publication/open only, while every actual header/body byte remains checked on
currentness; the budget was not enlarged. Development failures remain in the
verification record. Runtime/NKN behavior is outside this RCA.

The original GUI facts and pins remain required. No acknowledgment, post-GUI
survival, original-primary input capability or full-custody flag is issued.
Verification: `artifacts/frontier-parent-facts-20260916/RESULTS.md`.

## Implemented two-sided acknowledgment: A8b34

The original parent now acknowledges only its exact live fact capability and
native pending set; the original GUI binding confirms only the matching offer
and fact-header digests. Both operations are parameterless and one-shot, use
existing cumulative work budgets, and fail closed on replay, mutation or expiry.
The new fixed 80-byte envelope adds no content read or authority evaluation.

Review identified why acknowledgment must not silently enable post-GUI custody.
`Finalizer.Complete` first retires its original binding, then validates/encodes a
legacy scalar summary. `TakeBodyAfterExit` retires the provisional set before its
native successful-exit and completion-decoding gates. The legacy summary cannot
stand in for the complete original fact packet. Skipping those retirements or
ignoring the GUI-exit callback would leave pins usable after failed completion.
This checkpoint changes neither legacy method nor native cleanup callbacks.
Confirmation remains provisional, with all full-custody/live/publication flags
false and all original retirement behavior intact.
Verification: `artifacts/frontier-parent-acknowledgment-20260916/RESULTS.md`.

## Implemented completion-bound parent custody: A8b35

The parent now prepares a separate, bounded native escrow from the original
confirmed acknowledgment. GUI completion is parameterless and derives its inputs
from original immutable captures. Arming allows expected original GUI cleanup;
publication follows only successful unchanged legacy completion. A held native
process wait still rejects nonzero exit or missing completion. The parent exposes
usable custody only after its original one-shot take checks the exact committed
body, original request scope, immutable ledger/copy/writer facts and retained
native identities. Request disposal closes both pending and committed resources.
No original pending callback was bypassed and no reader or production call site
was added. Existing wrapper output/state guards remain the sole call-site gates;
these fixtures do not claim new production output/EOF verification.

Initial positive fixtures exposed a representation mismatch, not a native-file
ownership failure: the legacy decoder synthesizes output-role names, while the
owned capture uses unique creation paths. Matching the three native identities,
hashes, lengths and line counts is authoritative; reopening or treating those
legacy labels as actual owned paths would be incorrect. The analyzer-copy path
continues to match exactly. Both initial failures are retained in the checkpoint.
Verification: `artifacts/frontier-parent-commit-20260917/RESULTS.md`.

## Implemented primary-request custody: A8c1

The original committed parent can now perform the optional primary's original
source hash pass through its retained native copy, then transfer the exact full
native/fact set into the original unbound primary request. Exact completion
reference, request bytes and native source identity are required. Private mapped
readers own independent logical positions and close with that request, including
on native worker exit. This is parent-side request custody, not primary-worker
attachment or evaluator ownership. Production remains disabled.

The hash/transfer share the cumulative parent budget; mapped reader operations
share one bounded active-work clock. The initial PowerShell 7 position regression
identified repeated `SafeFileHandle` getter synchronization, fixed by retaining
the original request handle reference once. Legacy hash/open behavior is unchanged.
See `artifacts/frontier-primary-request-20260917/RESULTS.md`.

## Implemented native primary-worker attachment: A8c2

The exact request-owned set can now be offered after binding the actual worker.
The explicit frontier attachment verifies both process incarnations, immutable
request bytes, contributor/file topology and exact original facts. It pulls the
parent's current handles, never the historical GUI descriptor numbers. Partial
pulls and cancelled/stale owners release resources; existing publication requests
are excluded. Legacy attachment remains separate.

The A8c2 worker capability proved native attachment only and refused source
opening. A8c3 and A8c4 below add the native reader and array/view association
primitive. Diagnostic publication remains absent. No production caller is enabled.
See `artifacts/frontier-primary-worker-20260917/RESULTS.md`.

## Implemented native primary source: A8c3

The original worker now reads its retained semantic-copy handle through bounded
mapped windows, without shared-position changes or a path reopen. EOF requires
the original byte/line counts; exact emitted string references join the immutable
semantic-origin map. Cancellation, replay, partial consumption and stale native
scope close the optional resources. Legacy array/evaluation and writer-path
entry points remain explicitly unavailable for frontier workers.

The initial positive fixture exposed a framing mismatch: `WriteLine` separates
semantic records with CRLF but does not append a final separator. Reusing the
raw-ledger framer unchanged rejected the genuine final record. An explicit
semantic-tail mode fixes that edge; the raw-ledger caller still requires final
LF, and strict UTF-8 and line bounds apply to both. There is no extra source pass,
budget increase, production integration or authority evaluation.
See `artifacts/frontier-primary-source-20260917/RESULTS.md`.

## Implemented native primary array/view association: A8c4

The original completed source now admits a distinct one-shot evaluation custody.
It validates exact emitted references and ordered semantic ordinals, derives
private physical-origin maps and requires compact to be a subset of full. It
records full then compact views and binds one pair to the original context,
arrays, views and generated invocation identities. Subsequent ownership checks
reject array mutation and native revocation. Replay retires the optional set;
copied owners and foreign queries cannot consume or retire the original.

This helper does not invoke the evaluator, prove a supplied view's semantic
contents, freeze whole result objects or validate catalog completeness. The
trusted original primary seam remains responsible for supplying its own arrays
and results. No production call site, legacy publication issuer, additional
source/hash pass or live eligibility is introduced. The record and work budgets
remain bounded. See `artifacts/frontier-primary-evaluations-20260917/RESULTS.md`.

## Implemented native payload prerequisite: A8c5

The existing offline witness resolver cannot supply the next native join: it
accepts caller receipt JSON and uses a legacy writer reader and offline receipt
profile. Frontier workers correctly block that path. Before selecting a native
failure witness, the original primary needs its own native payload bytes.

A8c5 retains the already-validated writer manifests from the original fact decode
and captures payloads through the actual held read-only pins. Exact length/hash
and strict UTF-8 are checked in one bounded payload pass, with independent mapped
positions. One pair-owned native set is issued only after complete capture and
original array/native revalidation. Failure/replay/revocation clears the original
cached text and retires the optional set; copied observations cannot recreate it.
Catalog limits and the shared work clock are unchanged. No source rescan,
authority evaluation, production caller or publication capability is added.
See `artifacts/frontier-primary-payloads-20260917/RESULTS.md`.

## Implemented physical witness batch: A8c6

The original decoded cursor offsets are now retained, not re-read. A one-shot
batch binds 1–64 exact original full-array positions to the original pair and
native payload. It derives physical/compact/byte origins and hashes selected
decoded lines, and joins native completion/marker ranges to exact line boundaries.
Writer spans exclude their final Windows CRLF, while cursor offsets include it;
the join preserves that exact distinction. Invalid selection, changed arrays,
replay, budget exhaustion or revoked custody clears and retires the optional set.
No production call site or failure/verdict behavior changes. This does not prove
semantic receipt/failure matching. See
`artifacts/frontier-primary-witnesses-20260917/RESULTS.md`.

## Implemented semantic diagnostic join: A8c7

The native resolver consumes the trusted primary's already chosen unresolved
failure and existing proof, not a second evaluation or a substitute successful
episode. It joins the native receipt's source/repair/recent/root/successor/write/
frame/completion links to original lines and both original completion views.
Completion and marker selections must also match exact native physical ranges.
Relevant episode/stream coverage stays within the unchanged 64-witness budget.
The batch retains at most 16,384 UTF-16 code units of relevant original metadata
and clears that snapshot on retirement. Existing work/record limits remain in
force. Duplicate fields cannot satisfy required links; opaque diagnostics retain
their original text without granting authority.

The positive fixture uses a real service-generated unresolved failure and native
receipt, with exactly two original snapshot evaluations. It preserves the
original views and proof. No runtime fix, production caller or publication issuer
is introduced. Native currentness covers custody/arrays, not later deep mutation
of mutable proof/view objects; the separate output/final-publication gate must
recheck those relevant fields. See
`artifacts/frontier-primary-join-20260917/RESULTS.md`.

## Implemented local diagnostic preparation: A8c8

Native custody now captures and rechecks the relevant original failure/proof/
index/view note properties and array membership before/after semantic resolution
and on later currentness checks. The bounded guard does not execute script
properties, custom collection accessors or replacement-object equality methods.
It uses the existing cumulative work/record budget and caps retained observations
at 65,536. No second correlator or snapshot evaluator is invoked.

One original joined batch can prepare a local diagnostic capability containing
native-derived input descriptors and original request/worker/invocation/binding
identities. Copied objects, physical-only selection, mutation, replay, exhausted
budget and cancellation cannot acquire or preserve it. Retirement clears original
cached preparation data. This is not cross-process output/publication; final
publication must still recheck the original capability and preserve parent gates.
See `artifacts/frontier-diagnostic-preparation-20260917/RESULTS.md`.

## Implemented diagnostic transport: A8c9

Original primary-input cleanup closes its files on worker exit, so retaining that
same capability could not support a later diagnostic take. A8c9 leaves the
cleanup unchanged and prepares independent read-only parent escrow before exit.
The exact A8c8 preparation offers a bounded binary packet, the original parent
adopts it while the worker is alive, and the worker rechecks original native and
relevant analysis state before final commit. Frozen header/body comparison and
a private commit-token preimage prevent a state-only commit from passing take.

The parent takes transport once after successful exit of the exact held worker.
Native descriptors, selected payload and physical witness origins are checked
against original parent facts. The wire cap is 4 MiB; original snapshot/payload/
witness limits and worker budgets remain unchanged. Parent output operations
share five seconds of cumulative active work. Revocation, mutation, partial
output, failed/no-commit exit and replay omit diagnostics; request cancellation
still closes escrow after take. Copied capabilities carry no ownership.

Transport is not terminal-report acceptance and cannot issue legacy publication
completion. Original primary output EOF, source and terminal gates remain to be
joined before production opt-in. No extra authority evaluation or primary
verdict/count change is introduced. See
`artifacts/frontier-diagnostic-output-20260917/RESULTS.md`.

## Implemented original primary pipe ownership: A8c10

The existing retained parents own their stdout/stderr tasks, but frontier
transport does not own those reads. Accepting supplied task/EOF-success fields
would invent a new authority source. A8c10 instead adds an opt-in original
primary launch owner using the existing native raw-stream pumps and sealed
read-only output handles. It rejects already-started/bound launches and requires
both redirected streams; old launch and report paths are unchanged.

The exact original transport can join once only after native successful exit,
both original EOF/capture gates and current request/source/escrow ownership.
Inherited pipe writers prove why exit is insufficient. Copies, replay, changed
scope, closed pins and cancellation cannot retain the join. Revocation does not
close live pipes or kill the worker; ordinary capture drains under the existing
outer deadline. Retained output is limited to 64 MiB per stream. No new source
scan or authority evaluation is introduced. This is not terminal acceptance.
See `artifacts/frontier-primary-output-20260917/RESULTS.md`.

## Next steps

1. A8c22 uses A8c21's ready-aware owner in the original GUI entry before ledger/app
   startup. A8c23 supplies report-preserving app-start/drain branches and A8c24
   adds child-only app request delivery with native readiness. A8c25 adds bounded
   file-ready/adoption waits without consuming attachment/acknowledgment early.
   A8c26 adds background app-owned setup and the original final drain/handoff
   lifecycle without waiting during UI startup. A8c27 binds real service
   observations to that exact catalog and preserves capture closure/ownership.
   A8c28 adds bounded readiness for admitted positive streams and exact completion
   receipts without fixture-forced drain/log-tail settlement. A8c29 fixes original
   creator cleanup for contexts rejected before installation; existing normal
   terminal retirement is unchanged. A8c30 adds bounded app-owned enrollment
   of writer-only/non-admitted/retired streams with creation/shutdown fences.
   A8c31 adds an explicit launch-only scenario branch for both original app
   requests and the native initial cursor, preserving ordinary report tasks and
   cleanup ownership even when report setup fails. The live entry remains
   disconnected, and the branch cancels optional setup before ordinary cleanup.
   A8c32 now enables those original channels and schedules adoption after both
   close requests, before process-exit waits, sharing the unchanged cleanup
   budget. The original owner is forwarded privately through cleanup and retained
   until finalization returns. A8c33 seals those adopted files after output EOF
   and connects the matching finalizer binding and original final cursor/source
   processing. The binding/result stay private in that finalizer. A8c34 connects
   native semantic copy/evidence persistence with a distinct fixed copy path,
   and aligns the bounded selector count with the actual shared catalog. A8c35
   repairs the resulting native analyzer/legacy-label join at parent completion.
   Next schedule native parent delivery before legacy completion retires the original
   owning lifetime.
   The existing legacy completion/relay cannot reconstruct original native custody.
   Native file/fact/acknowledgment/commit scheduling must preserve one-shot checks,
   existing work budgets and watchdogs; do not poll consuming operations before
   their offers exist. Keep production callers disabled while those upstream
   launch/finalizer/cleanup seams and their failure paths are wired and tested.
2. Enable production opt-in only after that review and focused tests. The
   old completion schema and primary verdict/evaluation counts must stay unchanged.
   No live-256 test is justified by registration alone.

A8c35 reproduces and fixes the first failed parent-completion proof edge after
A8c34: the native copy's distinct creation filename no longer equals the legacy
codec's analyzer label. The join now requires both exact fixed paths under the
original artifact root, alongside the unchanged native identities/content facts.
The primary still reads the held native analyzer, never the legacy label by path.
Downstream regression tests also exposed terminal helpers and retained-analysis
entry points treating that label as a required filesystem file. Their ordinary
branches keep existence checks; only original native preparation/reader ownership
permits the logical label through the analyzer and parser. A raw path, copied
completion or opt-in flag alone cannot establish native input.
This changes no legacy schema, completion/exit/state gate, budget or authority.
Parent rendezvous remains unconnected. Its review also requires exactly one
completion path and primary transfer before the original request's `finally`
disposes custody; returning a legacy summary cannot preserve that lifetime.
See `artifacts/frontier-parent-handoff-20260920/RESULTS.md` and the A8c35 contract.

A8c34 calls the original copy/persistence gates before ordinary evidence work,
using a distinct native analyzer-copy destination and private evidence ownership.
Full-finalizer tests use the actual semantic catalog and ordinary ledger/copy/
persistence body, including native collisions and legacy exceptions. The current
catalog has 1,138 names / 56,098 code units; the shared native count ceiling is now
2,048, with the existing 65,536-code-unit combined metadata limit, 1,024 per entry,
work budget and 64-witness cap unchanged. Native cleanup review also found cached
channel state could predate attachment completion/exit and reject a valid ACK.
The closure check now observes ACK after those causal transitions, without polling
or relaxing failed-exit/cancellation guards. Before/after deterministic models
prove the read-order defect; the intermittent native failure did not capture its
exact interleaving. Further tracing found ordinary HasExited could skip a wait
before the original native process handle was signaled, despite completed ACKs.
One owner-selected native exit wait now precedes the unchanged completion checks,
outside locks and within the same remaining cleanup budget. A controlled native
early-HasExited regression reproduced that gap before the fix; timeout/cancel and
repeat remain fail-closed without budget renewal. No parent custody or runtime authority
follows from this diagnostic persistence. Native parent scheduling remains next.
See `artifacts/frontier-finalizer-persistence-20260920/RESULTS.md`.

A8c33 invokes the existing seal/bind/observed-cursor operations from the actual
finalizer after its cleanup/output drain, importing no legacy scalar bookmark,
snapshot, source path or success flag. Missing issuer, failed cleanup, rejected
source/cursor or optional processing failure retires native ownership while the
ordinary finalizer continues. Native results are not exported through the public
legacy capture or substituted for legacy output paths. The finalizer releases
them on failure/return; its existing completion also revokes original ownership.
No native copy, evidence persistence or parent custody is issued by preparation.
See `artifacts/frontier-finalizer-source-20260920/RESULTS.md`.

A8c32 adds a one-shot native wait for the original pending file submissions, not
an acknowledgment or custody issuer. It adopts either ready app, never polls a
consuming operation, and uses the remaining cleanup deadline during both idle
waits and active native checks. The original exit waits receive only the remaining
shared budget. Missing acknowledgment/attachment/successful exit or timeout retires
optional ownership before forced cleanup. Default actions and ordinary reports
are preserved. Live entry remains disconnected; adopted-file/final-cursor binding
and native finalizer/parent delivery remain next.
See `artifacts/frontier-cleanup-adoption-20260920/RESULTS.md`.

A8c31 carries original requests through the existing helpee/helper flows without
putting capabilities in the scenario context or ambient child scopes. The owner
selects its own native initial cursor; ordinary bookmarks are not imported into
it. The original report tasks reach finalizer output draining. A reproduced
report-setup failure now retains the started process for ordinary cleanup before
the error propagates. At that checkpoint the branch was launch-only: setup was
cancelled before cleanup until native adoption could be scheduled before waiting
for app exits. A8c32 supersedes that cancellation; no live caller is connected.
See `artifacts/frontier-scenario-launch-20260920/RESULTS.md`.

A8c30 retains original completion tasks, not contexts, for at most 16 services
and 64 streams per app lifetime. Enrollment precedes first emission; disposal
closes service creation, and finalization rejects open scopes before a one-shot
seal/wait outside ownership gates. Writer-only and retired streams share the
positive receipt wait budget. Overflow, timeout and late creation invalidate
diagnostics without suppressing the ordinary drain or granting route authority.
Fresh two-host native tests use real service creation and the original emitter
worker; 53 distinct focused/affected checks have passing latest results. Original
GUI request/cursor/finalizer and parent custody integration remain next. No live
caller is enabled. See `artifacts/frontier-producer-registry-20260920/RESULTS.md`.

A8c29 closes a prerequisite discovered during the broader producer review: a
context created but never installed now remains the creator's responsibility and
is disposed in its finally outside the service gate. Four pre-fix regressions
reproduced open streams on busy/correlation rejection and inbound/outbound setup
exceptions. Installed context ownership and ordinary terminal retirement remain
unchanged. This is not the all-producer shutdown barrier; that still needs bounded
original enrollment and late/in-flight creation fences. No live caller is enabled.
See `artifacts/coordinator-retirement-20260920/RESULTS.md`.

A8c28 waits outside ownership/capture/logging gates for the original admitted
stream's final message submission and exact completion persistence before markers.
Queue emptiness, task completion and writer high-watermarks cannot replace the
original persisted receipt. Timeout/revocation/failure reject the optional handoff
without skipping the ordinary drain. This is not all-app producer quiescence:
writer-only/non-admitted streams need the next ownership review. No live caller
is enabled. See `artifacts/frontier-shutdown-readiness-20260920/RESULTS.md`.

A8c27 adds original desktop service-factory binding and one admission attempt at
the first eligible fallback repair write. Native admission is outside the service
gate; exact context/data/contract/frontier checks fence attachment afterward.
Closed positives survive reset/disposal, incomplete captures stay rejected, and
actual capture gates close before preparation outside those locks. The next
shutdown readiness seam remains: real coordinator completion schedules a drain,
while current headless producer fixtures explicitly settle it before finalization.
No live caller is enabled. See `artifacts/frontier-service-admission-20260920/RESULTS.md`.

A8c26 connects the receiver to desktop startup behind the existing exact selector
and retains its process-owned catalog/writer beyond service disposal. Early exit
wakes setup before joining it; optional rejection still performs the ordinary
final drain. Exact original preparation/markers precede drain and native handoff,
adoption/acknowledgment and lease completion precede cleanup. Real service
observation admission and GUI/finalizer/parent integration remain next; no live
caller is enabled. See `artifacts/frontier-app-lifetime-20260919/RESULTS.md`.

A8c25 addresses the file-channel/adoption scheduling prerequisite discovered in
the app lifecycle review. Channels exist only after both apps are bound/sealed;
original app requests now own a pre-launch file-ready wakeup. The new internal
waits hold no ownership locks/work clocks, reject cancellation/GUI exit/timeout,
and still require the original channel attachment and acknowledgment checks.
Actual setup must run outside UI startup because the original GUI waits for the
first window before launching the second app. App-owned writer/drain lifetime
integration remains next. See `artifacts/frontier-file-rendezvous-20260919/RESULTS.md`.

A8c24 closes the early-app binding race without activating real app startup or
live scenarios. Child-only locators select a bounded ready-aware wait outside
ownership locks/work clocks; failure, cancellation and native parent exit reject.
The unchanged native attachment still validates the actual app and complete scope.
Collision/disposal preserves ordinary reporting and never causes another launch.
Next is the original app-owned lease/catalog/writer and drain/handoff lifecycle,
not a live-256 run. See `artifacts/frontier-app-start-20260919/RESULTS.md`.

A8c23 preserves ordinary app reports independently of optional raw capture/native
binding. Original startup and ordinary report failures are not hidden, and native
exit/EOF checks are separate from file quietness. Both original GUI helper branches
are explicit opt-ins; no scenario supplies them yet. Original app readiness and
request delivery, native cursor/finalizer/parent integration remain next.
See `artifacts/frontier-app-reports-20260919/RESULTS.md`.

A8c22 closes the original entry/ledger-creation mismatch without activating live
callers. Its output-only creation result preserves ordinary initialization after
optional post-create rejection; original pre-existing-file errors remain. Exact
native handles are private, not stored in mutable ledger state or passed to
formatters, and close even on original scenario-cleanup failure. App output and
native cursor/finalizer/parent delivery are still pending.
See `artifacts/frontier-gui-ledger-entry-20260919/RESULTS.md`.

A8c21 closes the early-attachment scheduling gap with a native readiness wait
outside ownership locks/work clocks and a default-off original parent branch.
Optional GUI startup setup/binding failure preserves ordinary reports; real start
errors propagate. A reproduced concurrent-disposal leak in the new readiness
resources is fixed under the launch lock. The actual GUI entry and later native
custody delivery are not activated or certified by this launch boundary.
See `artifacts/frontier-gui-start-20260919/RESULTS.md`.

A8c20 verifies 28 preparation/launch/capture failure cases and four affected
success regressions across both original parents and hosts. Readiness collision
remains a pre-launch failure without a retry or fabricated terminal receipt.
Budget-rejected preparation releases its own pins and permits ordinary legacy
analysis; capture/receiver collisions preserve reports and omit diagnostics.
Production code and live call sites are unchanged. This is selected failure-path
coverage, not complete upstream GUI or post-launch source-handoff certification.
See `artifacts/frontier-parent-failure-containment-20260919/RESULTS.md`.

A8c19 wires both original retained-analysis parents through source preparation,
native launch, watchdog-driven diagnostic adoption and exact receipt/persistence.
An absent offer does not consume adoption or a short launch-time deadline. Original
1800-second watchdogs, report tasks, status/error behavior and cleanup remain;
diagnostic evidence never approves the original unresolved proof failure.
See `artifacts/frontier-retained-parent-integration-20260919/RESULTS.md`.

A8c18 integrates the original compact analyzer while keeping live callers
disabled. It preserves the first failure, reports and the existing three evaluator
calls (the original pair plus a separate probe-sidecar subset evaluation).
See `artifacts/frontier-analyzer-integration-20260919/RESULTS.md`.

A8c17 removes diagnostic offer/adoption timing dependence from the genuine
headless terminal chain. Native signals only permit unchanged adoption and commit
checks; copied/replayed waits cannot acquire or revoke the original operation.
Both production parents remain disabled and unintegrated; A8c18 adds the analyzer
side only.
See `artifacts/frontier-diagnostic-rendezvous-20260919/RESULTS.md`.

A8c16 supplies bounded native source readiness before attachment and preserves
original reports after optional launch failures. The analyzer selector remains
default-off; production parents are not enabled. The new headless terminal chain
uses native source readiness, while its later diagnostic adoption/commit still
uses explicit fixture signals. See `artifacts/frontier-primary-start-20260919/RESULTS.md`.

A8c15 composes the original source preparation and verifies both parent identities
through controlled native worker/report/receipt/persistence integration. Production
still uses legacy attachment/path reading; native readiness and live diagnostic
adoption must be wired before enabling either caller.
See `artifacts/frontier-primary-preparation-20260919/RESULTS.md`.

A8c14 supplies the native source-count prerequisite, with actual strict-decoded
counts before request creation and no pathname reopen or position drift. It does
not wire the original parents or complete source acquisition/failure containment.
See `artifacts/frontier-primary-source-count-20260919/RESULTS.md`.

A8c13 adds an explicit internal report-launch opt-in to both parents; no production
call site enables it. Original report strings survive optional capture creation,
write/seal or source-binding failures, with no second launch or competing reader.
Actual read/start errors remain failures, process exit is not pipe EOF, and the
existing report encoding/status/watchdog/cleanup behavior is preserved.
See `artifacts/frontier-primary-reports-20260919/RESULTS.md`.

A8c12 now supplies the bounded persistence operation, but neither production
parent calls it. It must consume the exact current A8c11 result before parent
cleanup. It writes the original packet and bound metadata to a separate
diagnostic-only record with no overwrite, no authority flags and no extra
evaluation. Failed persistence cannot replace the primary result. The saved
capability dies with the original request; later file bytes are historical only.
See `artifacts/frontier-terminal-persistence-20260917/RESULTS.md`.

A8c11 keeps the original atomic receipt writer and status schema. Native source
readers replace only its two path-based post-analysis source passes when explicitly
supplied; restrictive sharing and file positions are preserved. Its terminal
binding compares native-derived original catalog fields and byte-exact host text
report projections, not raw-capture hashes. Copies, mutations, omitted hooks,
failed writes, replay and revocation cannot retain the result. It preserves
`proof_incomplete` rather than approving it, and all authority flags remain false.
See `artifacts/frontier-terminal-receipt-20260917/RESULTS.md`.

The cursor edge preserves the `Get-GuiSmokeLogLedgerSnapshotIntegrityReasons`
rules (ledger identity, indexed active generations, monotonic generation/writer
snapshots and valid writer identities/ranges) and the finalizer's cursor checks
(`GapCount`, `DuplicateIndexCount`, consumed-index versus final-line count and
`FailureCode`). The current PowerShell snapshot/diagnostic objects are mutable
observations, not original ownership receipts. A8b26 owns actual byte/index
observation operations; A8b27 derives immutable writer-prefix facts at the owned
bookmark and joins exact original LF indices and counts during the same pass.
The retained native source supplies the single fixed active generation; copied
semantic scalars or empty reasons arrays are not accepted. Legacy reader framing
is unchanged; the separate owned path is strict UTF-8/LF. No second source scan
or authority evaluation is added.

Verification: `artifacts/frontier-validation-snapshot-20260916/RESULTS.md` and
`artifacts/frontier-writer-match-20260916/RESULTS.md` and
`artifacts/frontier-recovery-inputs-20260916/RESULTS.md` and
`artifacts/frontier-recovery-markers-20260916/RESULTS.md` and
`artifacts/frontier-ledger-origin-20260916/RESULTS.md` and
`artifacts/frontier-ledger-cursor-20260916/RESULTS.md` and
`artifacts/frontier-cursor-match-20260916/RESULTS.md` and
`artifacts/frontier-semantic-copy-20260916/RESULTS.md` and
`artifacts/frontier-evidence-persistence-20260916/RESULTS.md` and
`artifacts/frontier-parent-registration-20260916/RESULTS.md` and
`artifacts/frontier-parent-preparation-20260916/RESULTS.md`.

A8b25's creation-versus-scenario-bookmark distinction and bounded continuation
are recorded in `docs/file-transfer-frontier-ledger-origin-rca.md`.
