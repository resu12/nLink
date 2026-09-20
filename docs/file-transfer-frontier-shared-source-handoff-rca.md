# Shared acceptance-log handoff: integration RCA and bounded fix sequence

Date: 2026-09-16. Base HEAD: `2691fc70f98d668bae943ebd41c6f8540156ec57`.
This analysis concerns the current accumulated, uncommitted A8b7/A8b8 implementation
and its disabled production integration. It is not a live-256 failure RCA and does
not reclassify an older artifact.

Current checkpoint (2026-09-20): A8c35 repairs a prerequisite found while reviewing
native parent scheduling. A current-tree positive parent-commit test reproduced
custody rejection: A8c34's distinct native analyzer filename no longer equaled
the unchanged legacy completion label. The join now requires both exact fixed
paths under the original completion artifact root, with unchanged content checks
and original native handle identities. Parent hashing and primary-worker reads
continue to use the held native file, never the legacy label as a locator. This
does not change the legacy codec, budgets, output/state/exit gates or authority.
The earlier A8c34 passing tests did not cover this later parent-commit edge.
Downstream tests then exposed the same physical-path assumption in terminal
hash/count helpers and the real retained-analysis entry points. The owned source
now bypasses legacy-file existence requirements only after original preparation
or reader acquisition; ordinary path-based input still requires its file. Native
candidate labels cannot become parser input before exact request attachment.
Focused verification is recorded separately in
`artifacts/frontier-parent-handoff-20260920/RESULTS.md`.
Native parent scheduling and consumption before original request disposal remain
unconnected; the live entry is still disabled. No live-256 test yet.

Prior checkpoint (2026-09-20): A8c34 connects original semantic copying and
diagnostic evidence persistence after native source preparation, before ordinary
finalization. A reproduced filename collision is removed by reserving
`filetransfer-frontier-retained-log-slice.log` for the native copy; the ordinary
analyzer path remains unchanged. Full-finalizer testing also exposed the real
1,138-name semantic catalog exceeding the native 1,024-selector cap. A shared
2,048-selector cap now covers it without changing the 65,536-code-unit combined
metadata ceiling, per-name bound, work budget or witness cap. Native persistence
stays private and diagnostic-only; completion/cancellation revokes ownership.
An intermittent native cleanup rejection also exposed a stale channel-state
read across attachment completion/exit. The closure check now samples ACK state
after those observations. Deterministic pre-fix/current models prove both valid
interleavings and retain rejection guards; the native failure itself did not
capture the precise interleaving. No polling, retry or authority fallback is added.
Further tracing found the remaining failure: ordinary cleanup reported an app
already exited while its original native process handle was not yet signaled.
Adoption and ACKs had succeeded and the deadline had not expired. Cleanup now
waits once for both original native exit signals, outside ownership locks and
within the same remaining cleanup budget, before its unchanged completion checks.
A controlled early-HasExited regression reproduced the failure before that fix.
Next schedule original parent delivery before legacy completion. Live entry
remains disconnected; no live-256 test yet.
Verification: `artifacts/frontier-finalizer-persistence-20260920/RESULTS.md`.

Prior checkpoint (2026-09-20): A8c33 connects source preparation inside the
original GUI finalizer's explicit optional branch. After cleanup and original
output EOF, that branch seals the adopted files, takes the matching original
finalizer binding, and invokes the existing owner-selected final cursor/source
processor once. The binding and result remain private, separate from legacy
snapshots and output captures. Failure retires optional ownership without
replacing ordinary finalization; finalizer completion/disposal ends the native
lifetime. Live entry remains disconnected. Next integrate original semantic
copy/evidence persistence and parent delivery before legacy completion; preparing
the source is not proof of those later gates. No live-256 run yet.
Verification: `artifacts/frontier-finalizer-source-20260920/RESULTS.md`.

Prior checkpoint (2026-09-20): A8c32 connects original file readiness/adoption
to the explicit scenario branch. After both close requests, one native wait
adopts either original pending submission without holding ownership locks during
idle waits. It shares the existing seven-second cleanup wait budget with later
process-exit waits; active pull/hash/control checks also see the remaining
deadline. Cancellation, early signals without submitted state, timeout or
incomplete acknowledgment/attachment/exit discard optional ownership before any
forced termination. Ordinary cleanup/report draining remains unchanged by default.
The original owner is forwarded privately through the finalizer's cleanup callback
and stays alive until that finalizer returns. The live entry still passes no owner.
Next bind original adopted files, native final cursor/source processing and
finalizer/parent custody; legacy output cannot substitute for that join.
No live-256 run yet.
Verification: `artifacts/frontier-cleanup-adoption-20260920/RESULTS.md`.

Prior checkpoint (2026-09-20): A8c31 adds an explicit, launch-only scenario
branch that carries both original app requests through the existing role flows,
captures the owner's native initial cursor after helpee startup and before the
helper launch, and retains original report tasks for final output draining.
Ordinary scalar bookmarks/snapshots remain unchanged and cannot replace the
native observation. A report-setup exception exposed a started-child ownership
gap; the native launch callback now records the actual process for cleanup before
report setup can throw, without retrying or masking its error.
The live entry still passes no owner. The new branch deliberately cancels
optional setup before ordinary cleanup: file channels/adoption and finalizer
delivery are not connected yet. Next schedule native adoption while both apps
can still submit, before blocking on their exits, then connect finalizer/parent
custody. No live-256 run yet.
Verification: `artifacts/frontier-scenario-launch-20260920/RESULTS.md`.

Prior checkpoint (2026-09-20): A8c30 adds bounded original app-owned producer
tracking before first emission: up to 16 services and 64 stream completion tasks
per app lifetime, without slot reuse or retention of transfer contexts. Service
disposal closes creation under the same gate used for enrollment. Finalization
requires all service scopes closed, seals once, and waits outside ownership gates
for every enrolled stream, including writer-only, retired and rejected contexts.
The stream wait and positive exact-completion receipt waits share the existing
five-second wait budget. Overflow, open scopes, timeout or late creation reject
the optional handoff; ordinary runtime and final writer drain remain available.
The original closed app owner remains discoverable so a late app factory cannot
silently become untracked. Catalog/native checks retain registry validity, not
new route authority. All 53 distinct checks have passing latest results.
Next connect original GUI request/cursor/finalizer and native parent custody
delivery. Live callers remain disabled; no live-256 run yet.
Verification: `artifacts/frontier-producer-registry-20260920/RESULTS.md`.

Prior checkpoint (2026-09-20): A8c29 fixes the first lifecycle prerequisite
found while reviewing writer-only/non-admitted streams. An inbound offer or
outbound setup that creates a context but never installs it now releases that
original context in the creator's finally, outside the service gate. Busy and
correlation rejection, setup exceptions, and disposal/duplicate installation
races no longer orphan their coordinator streams. Normal terminal replacement
already closed its original stream and was deliberately left unchanged.
The broader app-owned producer registry/wait is not yet implemented: enroll
original streams before startup, retain their bounded completion handles across
retirement, and fence in-flight/late creation before the ordinary final drain.
Live callers remain disabled; no live-256 run yet.
Verification: `artifacts/coordinator-retirement-20260920/RESULTS.md`.

Prior checkpoint (2026-09-20): A8c28 waits for each prepared observation's
original coordinator emitter to submit its final stream message, then requires
its exact original completion receipt to persist before marker submission.
The closed catalog shares one five-second readiness budget at the Program call
site; no capture/catalog/logging/native ownership gate is held during the waits.
Timeout or failed/revoked/copied receipts reject the diagnostic handoff while the
ordinary final writer drain still runs. Fresh native shutdown tests no longer
force the emitter drain or settle it through log-tail reads. This covers admitted
positive streams, not every app producer. Next review writer-only apps and
non-admitted/retired coordinator streams, then original GUI request/cursor/finalizer
and parent custody delivery. Live callers remain disabled; no live-256 run yet.
Verification: `artifacts/frontier-shutdown-readiness-20260920/RESULTS.md`.

Prior checkpoint (2026-09-20): A8c27 connects the desktop service factory to the
original app lifetime. The first eligible fallback repair write in an inbound
context makes one diagnostic admission attempt before physical I/O. Native
writer/catalog admission occurs outside the service gate; context, data session,
generation, frontier and original contract are rechecked before attachment.
Unready/drained/revoked writers cannot admit; incomplete capture cannot be retried
or replaced. Original capture gates serialize shutdown closure, with preparation
outside those gates and completed outcomes retained through reset/disposal.
Thirty distinct focused checks have passing latest results. No live GUI caller is
enabled. The next prerequisite is actual coordinator-emitter/completion-writer
readiness at shutdown: current fixtures explicitly settle the emitter, whereas
real stream completion queues an asynchronous drain. Then connect original GUI
requests, cursor/finalizer and parent custody. No live-256 test is required yet.
Verification: `artifacts/frontier-service-admission-20260920/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c26 adds the process-owned background receiver
and final drain/handoff lifetime. The desktop entry starts it only for the exact
existing selector; ordinary startup does not await either native readiness gate.
Early exit wakes and joins setup before the unchanged final log drain. Original
catalog admission closes before bounded preparation/markers, then original files
are submitted and acknowledged before lease completion and cleanup. The owner is
not disposed with the file-transfer service. Optional rejection preserves the
ordinary drain, and copied/repeated owners cannot finalize it again.
This is not live activation: real service observation admission is still unwired,
and the tests supply deterministic original producer observations. Next bind those
observations to the exact app-owned catalog, then deliver original GUI app requests,
cursor/finalizer and parent custody within their owning lifetimes. No live GUI
caller is enabled; no live-256 test is required.
Verification: `artifacts/frontier-app-lifetime-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c25 closes the next scheduling prerequisite for
app-owned writer integration. Each original app request now owns a pre-launch
file-ready wakeup; only the sealed two-app GUI channel setup publishes it. Apps
can wait before catalog creation without consuming their original file-attachment
attempt. Submitted files can wait for native adoption before the original one-shot
acknowledgment. Both waits are bounded, hold no ownership locks/work clocks and
fail closed on cancellation, native GUI exit or timeout. Signals do not replace
exact channel/attachment/submission/acknowledgment checks. Focused regressions
also reproduce the old under-mutex submission/adoption wakeups; both now signal
only after native/local locks are released. Real startup remains
unwired: a synchronous file wait on the UI thread would deadlock the existing
first-window-before-second-app flow. Next is background app-owned setup and an
ownership lifetime spanning service disposal and the final operational-log drain.
No live caller is enabled; no live-256 test is required.
Verification: `artifacts/frontier-file-rendezvous-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c24 delivers the app endpoint/request selector
only in the original report-preserving launch's child environment. Fresh native
ready/failed signals wake early apps after actual binding and ordinary reader
setup. A bounded wait holds no request mutex/work clock; cancellation, native GUI
parent exit and failure take precedence, then the unchanged exact attachment
checks remain mandatory. Collision/disposal omits the native path while ordinary
reports survive. Both shared-source compilers and Core include the same receiver.
The real app entry does not consume it yet: exact lease/catalog/writer ownership
and drain/handoff cleanup must be wired together before that opt-in. Actual GUI
scenario request delivery, cursor/finalizer and parent custody lifetime remain
next; live callers stay disabled and no live-256 test is required.
Verification: `artifacts/frontier-app-start-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c23 adds a single-launch, report-preserving app
path and default-off branches in the original app-start/drain helpers. Ordinary
line files update while the app runs; optional raw capture/binding failure cannot
lose those readers or trigger another launch. Actual start, report setup and
ordinary report-write errors remain failures. Original native exit and both pipe
EOFs are separate; quiet files cannot replace report-task completion. No live
scenario supplies app requests/reports to this branch yet. App-side readiness,
original native cursor observations, finalizer and parent custody delivery remain
next, without changing route/runtime authority or requiring a live-256 test.
Verification and test history: `artifacts/frontier-app-reports-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c22 consumes the ready-aware owner in the
original GUI entry before log creation. The original append-only initializer can
use the exact owner's CREATE_NEW operation while keeping its ordinary state and
readers. An output-only creation flag preserves that file if optional native
checks fail after creation; it cannot establish custody. Early rejection uses the
unchanged ordinary creation path. Pre-existing files remain errors, and cleanup
closes the owner even if scenario cleanup throws. Native capabilities stay out of
ledger diagnostics and formatter child scopes. No live parent opts in. App-output
capture, original cursor observations, finalizer and parent lifetime delivery are
still unintegrated; no live-256 test is required by this checkpoint.
Verification and test history: `artifacts/frontier-gui-ledger-entry-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c21 adds native GUI launch readiness and an
internal default-off branch in the original hard-deadline parent. One launch owns
the ordinary report tasks, binds the actual GUI, registers its parent endpoint and
signals readiness. Early attachment waits outside the request mutex/work clocks,
then runs the unchanged one-shot native identity/scope checks. Optional readiness
or registration failure cannot replace ordinary GUI startup. This differs from
A8c16's strict prepared-source launch rejection. A reproduced signal-creation/
disposal race is fixed by sharing the launch lock during cleanup. No live caller
enables this branch; original GUI entry, ledger/app/finalizer integration and
native custody delivery into the retained-analysis lifetime remain next.
Verification and test history: `artifacts/frontier-gui-start-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c20 verifies failure containment in both original
retained-analysis parents: expired preparation releases custody and uses the
original legacy source passes; readiness collisions, stale native inputs and a
missing executable fail without a worker/receipt; capture and receiver collisions
preserve ordinary analysis and omit diagnostics. All 28 new failure cases and four
affected success cases pass on both hosts. No production code or A8c16 rejection
contract changed. In particular, readiness collision is a required pre-launch
failure, not permission for a fallback restart. Live callers remain disabled.
Next: original GUI-launch/ledger/app/finalizer ownership and native parent custody
delivery; the legacy completion/relay cannot supply that chain. This is not a
live-256 result or complete upstream failure-containment certification.
Verification: `artifacts/frontier-parent-failure-containment-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c19 connects both original retained-analysis
parents to exact native source preparation, one report-preserving launch,
diagnostic adoption, the original terminal receipt and optional persistence before
cleanup. A nonblocking offered-signal probe runs inside the original 5000-ms
process-watchdog loop; an absent offer consumes no adoption or active-work budget.
The 1800-second deadline is unchanged and no 20-second offer wait starts at launch.
Signals still cannot substitute for packet, custody, commit, actual exit or EOF
checks. Failure/proof-incomplete receipts remain failures; saved diagnostics are
historical only. Live call sites remain disabled. Next: review original GUI-parent
custody delivery and pre-launch rejection/failure containment before enabling any
caller; this is not live-256 evidence.
Verification: `artifacts/frontier-retained-parent-integration-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c18 integrates the original compact analyzer
with native evaluation custody, payload selection and diagnostic offer/commit.
It retains the actual array already used to select the first failure; a generic
candidate list is not substituted for that native-observed array. The original
complete semantic read also supplies both bridge-startup raw-evidence scans,
without reopening its pinned path or dropping raw evidence. Reports, selected
failure and evaluation calls remain primary-owned. The genuine analyzer baseline
has the full/compact pair plus an existing probe-sidecar subset evaluation (three
calls total); this integration must add no fourth call. Original parent launch,
adoption timing, terminal receipt and persistence integration remain next. Both
production callers stay disabled; no live-256 run is justified by this checkpoint.
Verification: `artifacts/frontier-analyzer-integration-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c17 adds native offer/adoption wakeups after
the existing channel mutex is released, asynchronous original-parent adoption and
a worker wait before unchanged commit validation. Idle waits hold no ownership
locks, consume no active-work budget and reject on request/channel cancellation,
held peer exit or the existing producer deadline. Signals do not replace native
packet/custody/token checks. Both parent identities reach terminal persistence in
the headless chain without diagnostic fixture release signals. Production callers
remain disabled; original-parent failure containment and full diagnostic/terminal/
persistence scheduling are next, not a live-256 run.
Verification: `artifacts/frontier-diagnostic-rendezvous-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c16 composes one original report-preserving
launch, actual worker binding and native input/optional receiver preparation,
then publishes source readiness. A bounded wait outside the request mutex keeps
early workers from consuming attachment before handoff. Cancellation, failure or
parent exit rejects; ready only permits the unchanged native attachment checks.
Ordinary reports survive optional failures; no retry or authority is introduced.
The analyzer selector is default-off and neither production parent calls the new
launch. Diagnostic offer/adoption/commit readiness and original-parent terminal/
persistence integration remain next; no live-256 activation.
Verification: `artifacts/frontier-primary-start-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c15 composes exact original custody/completion,
native hash, actual source count, writer-bound request creation and full native
input transfer into one parent-owned preparation. It shares the original parent
work clock, releases the temporary hash lease and closes partial resources on
failure. Copies/foreign arguments cannot consume or retire the original. Native
headless integration covers both parent roles through terminal persistence.
Production callers remain disabled: legacy worker attachment still reopens the
source path. Native launch readiness/attachment, live diagnostic adoption and
original-parent failure/terminal integration remain next; no live-256 approval.
Verification: `artifacts/frontier-primary-preparation-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c14 adds the missing original pre-analysis
line-count pass on the exact native hash lease. Strict decoding/BOM/line behavior
is preserved without a pathname reopen, extra hash or shared-position change.
The genuine headless chain feeds the actual count into request creation before
custody transfer. This is a source-read prerequisite, not completed production
parent wiring or authority. Source acquisition/failure containment, diagnostic
adoption and terminal/persistence remain next; live-256 remains disabled.
Verification: `artifacts/frontier-primary-source-count-20260919/RESULTS.md`.

Prior checkpoint (2026-09-19): A8c13 adds a report-preserving primary launch and
default-off internal branches in both original retained-analysis parents. Optional
capture/binding failure cannot discard ordinary reports or cause a second launch.
The normal host text-report encoding, process outcome and watchdog remain. No
production caller enables this path. Original native source handoff, diagnostic
adoption and exact receipt/persistence wiring inside the parent lifetime remain
the next integration work; no authority or live-run approval is added.
Verification: `artifacts/frontier-primary-reports-20260919/RESULTS.md`.

Prior checkpoint (2026-09-17): A8c12 adds one-shot diagnostic persistence from
the exact current terminal result before request cleanup. It preserves the
original native packet and bound receipt metadata in a separate bounded,
no-overwrite record. Saved files cannot recreate native ownership and all
authority flags remain false. Production parents remain unchanged. Their next
gap is ordinary output-report preservation when optional native pipe custody
fails, including a launch that has already started before returning null.
Verification: `artifacts/frontier-terminal-persistence-20260917/RESULTS.md`.

Prior checkpoint: A8c11 binds the exact native output/transport join
to the original terminal receipt writer, its successful atomic write and exact
returned receipt. Native catalog fields and host-correct text report hashes join
the original source/worker identity. Native read-only source streams support the
same original receipt hash/count passes without reopening or relaxing file sharing.
Original statuses, failures/counts and all authority flags remain unchanged.
Production parent integration and optional persistence remain disabled and next.
Verification: `artifacts/frontier-terminal-receipt-20260917/RESULTS.md`.

Prior checkpoint: A8c10 owns the original primary's first stdout/
stderr reads through the existing native raw-stream pumps and joins both EOFs
to the exact original diagnostic transport. Native successful exit is necessary
but insufficient when a pipe remains inherited. Replay, copied objects, capture
failure, closed pins, changed scope and request cancellation cannot preserve the
join. Old launches/reports remain unchanged. Original receipt writing, source/
result agreement and terminal-report acceptance remain next; production/live
and publication eligibility stay disabled.
Verification: `artifacts/frontier-primary-output-20260917/RESULTS.md`.

Prior checkpoint: A8c9 adds separate bounded diagnostic transport.
Independent parent escrow survives normal worker-input cleanup without changing
it. Original preparation offers, parent adopts while the worker is alive, and
worker commit rechecks the original relevant/native state. One exact transport
can be taken after successful native worker exit; cancellation, replay, malformed
data and failed/no-commit exit omit it. A transport receipt is not terminal-report
acceptance. Original primary output EOF/source/terminal gates remain next, and
all production/live/publication flags remain disabled.
Verification: `artifacts/frontier-diagnostic-output-20260917/RESULTS.md`.

Prior checkpoint: A8c8 binds bounded original failure/proof/view/
index observations to native currentness and issues one local diagnostic
preparation with descriptors derived from held native input files. Mutation,
replay, budget exhaustion and cancellation retire it; copied objects and
physical-only selections cannot acquire it. No executable PowerShell properties
or extra evaluations run. Separate parent output transport/adoption/consumption
and final-publication revalidation remain next. Production/live stays disabled.
Verification: `artifacts/frontier-diagnostic-preparation-20260917/RESULTS.md`.

Prior checkpoint: A8c7 joins the trusted primary's original reported
failure and existing unresolved proof to its native receipt, original emitted
lines and full/compact completion evidence. Exact native ranges and relevant
episode coverage stay within 64 witnesses, with no additional authority
evaluation. A bounded relevant-field snapshot is retained in the original batch
and cleared on retirement. Separate native input descriptors/output and
publication-time revalidation of mutable proof/view fields remain required.
No production/live activation or failure/verdict change is introduced.
Verification: `artifacts/frontier-primary-join-20260917/RESULTS.md`.

Prior checkpoint: A8c6 captures one original evaluation/payload-bound
batch of at most 64 exact physical witnesses. The original validated cursor index
supplies byte positions; no new source scan or caller-origin import is added.
Native completion/marker spans join exact Windows CRLF boundaries. Revocation,
mutation and replay retire the batch; copied metadata remains diagnostic-only.
This is not semantic receipt/failure matching. That join, relevant-field snapshots,
native input descriptors and separate output remain next; production/live stays
disabled. Verification: `artifacts/frontier-primary-witnesses-20260917/RESULTS.md`.

Prior checkpoint: A8c5 supplies the native payload prerequisite for
witness preparation. It reads held payload pins once through read-only mapped
windows, verifies original manifest hashes and strict UTF-8, and retains one exact
evaluation-pair-owned set. No caller receipt JSON or path is imported. The offline
witness resolver remains separate. Original-failure selection, native receipt/
completion links, native input descriptors and diagnostic output remain later
gates. Production/live eligibility stays disabled. Verification:
`artifacts/frontier-primary-payloads-20260917/RESULTS.md`.

Prior checkpoint: A8c4 adds a separate original-array/view association
primitive. It derives native physical origins from exact emitted references,
records full then compact views and binds one exact invocation pair. Array
mutation, replay and revoked custody fail closed. This is not semantic validation
of arbitrary supplied views or production integration. Bounded original-failure
witness capture, native descriptors and separate diagnostic output remain later
gates; legacy publication and live eligibility remain disabled. Verification:
`artifacts/frontier-primary-evaluations-20260917/RESULTS.md`.

Prior checkpoint: A8c3 reads the original worker-owned semantic-copy
handle using bounded mapped windows, with exact emitted-line ownership and
immutable physical-origin joins after admitted EOF. It preserves semantic-copy
final-record framing without relaxing the raw-ledger final-LF rule. Original
array/view/invocation ownership and diagnostic output remain absent; legacy
evaluation/writer-path entry points are blocked for frontier workers. No production
caller is enabled. Verification: `artifacts/frontier-primary-source-20260917/RESULTS.md`.

Prior checkpoint: A8c2 attaches the original native primary worker
to A8c1's complete request-owned file/fact set. It verifies exact request/process
scope, rebases only the parent's current native descriptor numbers and imports
read-only handles through the actual held parent process. Source-reader adoption,
original input/view association and diagnostic output remain absent; no path
fallback or production caller is enabled. Verification:
`artifacts/frontier-primary-worker-20260917/RESULTS.md`.

Prior checkpoint: A8c1 transfers A8b35's exact committed native/fact
set into the original unbound primary request, with a native replacement for the
original source hash pass and private independent-position readers. Custody now
survives the old GUI-parent request lifetime, but remains in the parent process.
Native primary-worker attachment, input/view association and diagnostic output
are still absent. Verification: `artifacts/frontier-primary-request-20260917/RESULTS.md`.

Prior checkpoint: A8b35 adds bounded parent escrow and exact
completion/exit-bound custody to A8b34's acknowledgment. Original provisional
cleanup remains intact; a separate read-only set can survive successful GUI
retirement only through the original parent's completion take and immutable fact
join. Failure and request cancellation close that set. Legacy completion is a
gate, not a replacement for the exact private native/fact owner.
Legacy ledger JSON/completion schemas are unchanged and not upgraded. Primary custody remains incomplete; this is
not publication approval. The findings below
describe the original A8b7/A8b8 integration hazard; they are not a claim that
A8b9–A8c5 are still absent.
Production remains disabled.

Finalizer-edge review (A8b17): attachment establishes original finalizer ownership,
not successful validation/copy/persistence. The raw shared acceptance source and
the finalizer's ledger/semantic outputs are separate native files and must not be
relabeled as one another. The implemented one-shot binding joins the original
finalizer and sealed-files owner by the original native endpoint; same scalar
scope on another endpoint is insufficient. Legacy completion or disposal retires
only the matching optional set. The next missing edge is the separate output
handoff and native raw-source origin mapping, followed by original-parent custody.
Source correction (A8b18): the generated outputs are separate, but the append-only
processor input and both app writers use the same configured acceptance-log path.
The old processor reopens it independently, so pathname equality is not original
native file ownership. The new reader addresses this missing access edge without
claiming parser validation or output publication. See A8b18 in the payload-channel
contract and `artifacts/frontier-finalizer-source-20260916/RESULTS.md`.

Processor-edge review (A8b19): input EOF is insufficient to prove parsing, so the
new path requires an untouched original source, exact declared line count and
physical EOF after parsing. Its full work interval shares the original cumulative
GUI budget, and output-line origins are recorded during writes rather than joined
later by text. The processor's output captures still hold creation-time metadata,
not immutable native output readers; that is the next missing custody edge. See
`artifacts/frontier-ledger-processing-20260916/RESULTS.md` and A8b19 of the payload
contract. No production finalizer call site has been changed.

Output-lifetime review (A8b20): the new retained-output entry duplicates the actual
creation handles with reduced read-only access before closing the writable
originals. Native sharing exclusion stays in force, so a creation-time digest is
not later associated with a replaced output path. Exact original-source retirement
closes all three retained outputs; a stale output revokes the optional result and
source when observed. This closes output custody only, not finalizer validation,
copy/persistence or parent/primary adoption. See A8b20 in the payload contract and
`artifacts/frontier-retained-outputs-20260916/RESULTS.md`.

Validation-facts review (A8b21): the public processor result still exposed mutable
writer/drain/batch objects after output retention. The new private snapshot freezes
those facts and original counters before returning, preserving separate processor
and writer-closure errors. It cannot approve missing native membership, recovery
markers, cursor validation, copy or persistence. The complete gate inventory is
in `docs/file-transfer-frontier-finalizer-gates-rca.md`; verification is in
`artifacts/frontier-validation-snapshot-20260916/RESULTS.md`.

Writer-membership review (A8b22): immutable parser facts alone did not bind their
reported writers to the original adopted native contributions. The original
finalizer binding now owns a one-shot typed join with exact source/result/snapshot
identity, closed two-writer membership and terminal drain-sequence equality.
Copies and foreign results cannot consume the original attempt; an original
failed/repeated/late join revokes the optional set. The match is kept inside the
same processing budget and remains dependent on all original source/output
owners. Legacy processor load order and completion remain separate. See
`artifacts/frontier-writer-match-20260916/RESULTS.md`.

Recovery-input review (A8b23): original pump EOF previously closed the only owned
capture handles, so reopening diagnostic paths could not establish original
marker custody. Each pump now reduces its actual creation handle to a read-only
duplicate before close, retaining native write/delete exclusion. The original
finalizer can take exactly four scoped readers once; failures, repeated opening,
closed handles or original retirement discard the optional set. Raw I/O shares
the cumulative source/adoption budget without stopping an active processor clock.
This is raw-byte custody only, not marker parsing, tuple matching or recovery
approval. See `artifacts/frontier-recovery-inputs-20260916/RESULTS.md`.

Recovery-marker review (A8b24): retained raw bytes alone did not establish unique
deferred/recovered pairing, terminal-failure absence or correspondence with the
processed batch. The original finalizer now owns bounded UTF-8 parsing and key
selection before ledger processing, then joins exact immutable batch facts under
the same cumulative work interval. Every marker's writer must belong to its
original captured app incarnation. Original failures retire all optional inputs
and outputs; copies cannot consume or retire original attempts. This closes only
the recovery subgate, not ledger/cursor history, semantic copy, persistence or
parent custody. See `artifacts/frontier-recovery-markers-20260916/RESULTS.md`.

Fresh A8b24 compatibility tests also exposed a decoder EOF defect: Framework
`StreamReader` accepted an incomplete UTF-8 tail despite exception fallback.
Both the synthetic parser test and the original native-output fixture reproduced
this on PowerShell 5.1, while PowerShell 7 rejected it. The optional parser now
reads bounded byte chunks and explicitly flushes its strict decoder at physical
EOF. Both hosts pass the formerly failing cases. This is a fresh optional-path
finding, not a reclassification of historical live artifacts or a change to the
legacy finalizer.

## Original first missing link

The actual GUI owns two app launches but currently has no early original native
launch registry connected to the proposed frontier handoff. Its legacy writer
issuer is acquired inside `Write-FileTransferGuiLogLedgerEvidence`, in cleanup,
not before the app launches. See `tools/GuiSmoke-Windows.ps1` around the finalizer's
`LocalGuiWriterRequest.TryAttach` and `ProcessOutputDrainAction` calls.

`Start-AppInstance` obtains role-specific environment overrides, but
`Get-AppLaunchEnvironmentOverrides` assigns the same
`GuiSmokeAppLogLedger.AppendOnlyPath` as `NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH`
for both roles. The source is therefore shared, not independently sealable by
either app's own drain.

`OperationalLogNativeAppend.Write` captures the original drain append handle
and `TryFreeze` tightens native sharing to read-only. That denies further writes
from the other app. This happens during the first app's `CompleteAndFlush` receipt
path, before `TryAcquireSource` is called. Waiting to transfer the already-frozen
source does not solve the problem. A8b8's `TryWritePayloadFiles` currently requires
this app-local source custody, and its delete-on-close payloads cannot be recovered
by pathname after the app exits.

The isolated A8b7/A8b8 tests proved their app-local invariants, not compatibility
with this shared-writer topology. Directly connecting those entry points to live
cleanup could suppress another app's marker/drain and cannot be approved as-is.

## Deterministic reproduction

`NativeSourceSharedLogCannotBeSealedAtOnlyOneAppDrain` uses two independent real
writer pumps/loggers targeting one artifact-owned file. After the first writer's
exact source-retaining drain, the second writer's exact marker append fails. Both
the pre-acquisition and post-acquisition variants exercise this. Releasing the
first source permits a new append but cannot upgrade the failed marker receipt.
This fixture intentionally records a hazard, not a desirable production policy.

No runtime activation, GUI or network experiment is necessary to establish this
local file-sharing conflict. Focused artifacts and any fixture corrections are
recorded in `artifacts/frontier-gui-launch-20260916/RESULTS.md`.

## Implemented first step: early launch ownership only

The separate `LocalGuiWriterFrontierLaunch.cs` partial binds the original GUI
request before finalization without consuming/changing the legacy finalizer.
It retains original parent/GUI incarnation checks, then duplicates the actual
launched app process handles. Exactly two distinct app bindings must be sealed
before successful native exit evidence is available. Invalid bindings, a third
app, missing app, closed/cancelled/mutated scope and original parent death fail
closed for diagnostics. Copied wrappers cannot issue evidence or dispose originals.
Revocation never stops apps or signals cancellation to the old finalizer.

This is a primitive with offline real-process tests, not a production call site
or a completed app-request/catalog join. All live/full-custody/publication flags
remain false. Process exits alone do not prove drained writers, output EOF or
file custody. Supplied launch objects are trusted workflow inputs; OS parenthood
and hostile same-account isolation are not claimed.

## Remaining bounded sequence

Follow-up A8b10 (2026-09-16): the original early GUI owner now privately creates
and binds both native app requests; copied endpoints/DTOs cannot import that
ownership. Both native attachment completions and successful exits are separately
gated from process-only exit evidence. The old app/GUI wire formats remain intact.
Aggregate file/range/byte/work limits are now explicit in A8b10 of the payload
channel contract. This closes the request join only, not provisional file transfer
or source sealing. See `artifacts/frontier-gui-app-join-20260916/RESULTS.md`.

Follow-up A8b11 (2026-09-16): a native provisional source-anchor primitive now
retains the actual file with read-only access and read/write/delete sharing.
Another real process can keep that anchor while a writer persists marker/drain;
both delete and rename rotation remain available. Read/write sharing alone failed
the new rotation regression and was corrected before any runtime integration.
The primitive has no writer/catalog/GUI caller and supplies no final hash or seal.
The next channel uses GUI-side pull duplication, all-or-none acknowledgment and
process-local handle cleanup. See A8b11 of the payload-channel contract and
`artifacts/frontier-provisional-source-20260916/RESULTS.md`.

Follow-up A8b12 (2026-09-16): the fixed version-1 manifest and internal GUI-pull
primitive now validate bounded descriptors and retain both apps' native files,
with immutable payload digest checks, writable/rotatable source retention and
whole-set rollback. The same implementation runs in Core and PowerShell 5.1/7.
This closes only the wire/native-file layer: synthetic test metadata is not
original receipt authority, and no production caller or request rendezvous exists.
The next edge is original-request registration/readiness, one immutable submission
and exact acknowledgment before app-lease/catalog retirement. See A8b12 of the
payload-channel contract and `artifacts/frontier-file-handoff-20260916/RESULTS.md`.

Follow-up A8b13 (2026-09-16): the original sealed GUI registry now owns separate
file endpoints; the original Core lease/catalog can attach, submit pinned native
handles and take one exact acknowledgment before attachment completion. Control
and pull work share one budget. Late/mutated/replayed/failed handoffs revoke the
whole optional set without changing runtime work or the old GUI finalizer.
These are protocol tests with synthetic receipt data, not production writer
output. The remaining first edge is the private writer-owned submission issuer,
including explicit writer-only ownership. See
`artifacts/frontier-file-rendezvous-20260916/RESULTS.md`.

Follow-up A8b14 (2026-09-16): the private original catalog now issues the bounded
manifest from genuine prepared payloads and exact persisted writer receipts.
Only original handoff-bound registrations retain a provisional native drain
anchor; another app can keep writing and native rotation remains possible.
Writer-only contributions have their own original token and real drain, not an
empty positive catalog. Late writes revoke already-submitted optional output.
See `artifacts/frontier-genuine-submission-20260916/RESULTS.md` for native two-app
and pump verification. No production capture path was activated.

The new two-positive-app fixture also exposed unretired delayed grace tasks in
its manually constructed context. While the second app prepared, those tasks
attempted post-drain diagnostic writes in the first app, and the strict original
writer guard revoked it. The fixture now cancels its own context lifetime before
the writer drain; runtime callback/retry behavior is unchanged. New write attempts
also cancel an acknowledged/completed app handoff, distinct from normal cleanup.
Eventual production integration must honor real shutdown/drain/EOF ownership;
neither this test correction nor provisional adoption proves that integration.

Follow-up A8b15 (2026-09-16): final-seal review found that original process handles
alone did not supply original output EOF ownership. The existing production
PowerShell event captures/quiet-file polling cannot be imported as that capability.
An opt-in original-request start method now owns the first reads of both app
streams and their raw capture/flush completion. Failed sinks and revocation keep
draining without granting EOF evidence or stopping apps. Exit-before-EOF and
EOF-before-exit remain separate. This optional path is not wired into production;
the existing launcher/finalizer are unchanged. See A8b15 of the payload-channel
contract and `artifacts/frontier-output-ownership-20260916/RESULTS.md`.

Follow-up A8b16 (2026-09-16): the original GUI now joins both acknowledged genuine
writer/file contributions with successful attachment/native exits and original
output EOF. It seals the retained actual native source, verifies every original
raw range and one whole-source digest in a single pass, and retains a separate
reference-owned, revocable result. All work shares the existing cumulative GUI
budget. Failure/repetition discards the optional set; no pathname rediscovery or
runtime effect is added. This is not finalizer output or primary/parent custody.
See `artifacts/frontier-final-source-seal-20260916/RESULTS.md`.

1. Complete the distinct bounded GUI output and original primary/parent custody
   channel, with cancellation/partial-output/replay/mutation tests and unchanged
   primary verdicts/evaluation counts. Legacy completion remains unchanged.
2. Perform the end-to-end correctness/cost review before production opt-in, a
   portable rebuild and the next explicitly gated targeted live-256 run.

Do not split operational logs, delay app shutdown speculatively, add retry/queue
tuning, weaken receipt requirements, or treat exit/SHA/fallback completion as
route authority. No live run, commit or push is part of this checkpoint.
