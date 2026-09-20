# Original ledger/cursor ownership: first-edge RCA and bounded sequence

Date: 2026-09-16. Base HEAD: `2691fc70f98d668bae943ebd41c6f8540156ec57`.
This is a current working-tree integration review, not a new live-256 failure.
Production integration remains disabled.

## Original finding (before A8b25)

The optional finalizer owns its sealed raw source, processor outputs, immutable
writer facts and recovery-marker match through A8b24. It does not own the earlier
ledger/cursor observation operations. `Enable-GuiSmokeAppendOnlyLogLedgerState`
creates the source, closes its creation stream and stores path-derived generation
identity and mutable counters in a PowerShell object. Later synchronization
reopens that path and updates the same mutable state.

Copying `Get-GuiSmokeLogLedgerSnapshot` or diagnostic fields into immutable records
at finalization would prevent subsequent edits to those copies, but would not
establish original creation, original cursor operations, or original observation
timing. Accepting an empty integrity-reasons array would import a success verdict.
Neither approach closes the ownership gap.

The scenario's initial snapshot is taken after helpee startup, before helper
startup. An empty file at creation is therefore not the scenario's initial
snapshot or its bookmark. This distinction must remain explicit.

## A8b25 implemented boundary

`FrontierLaunchOwner.TryCreateLedgerOrigin()` is an explicit optional operation
before any app request or launch. It accepts no path, stream, native handle,
snapshot, scalar identity or success Boolean. The destination is the fixed
`live-operational-log/nlink.log` under the original registered artifact root.
`CREATE_NEW` preserves any existing file; a reparse-point log directory is rejected.

The original native creation handle requests read access only, with read/write
sharing and no delete sharing. This permits app writes while excluding source
replacement. The exact handle remains owned from creation, without a pathname
reopen or a writable duplicate. Kernel-level tests prove its write access is
denied. It must initially be empty and remains bounded by the existing 256-MiB
source limit. No raw read/seek/handle export is exposed.

Creation work starts the same cumulative five-second stopwatch subsequently
passed to the original file adopter. It does not reset that clock; idle app
runtime is not charged. Standalone transport tests retain their existing private
clock through the unchanged four-argument constructor.

When an origin exists, the source seal must match its actual native file identity
against both adopted writer contributions under the same budget. Identical bytes
in another file are insufficient. The read-only creation handle coexists with
the final restrictive source seal. Only the original sealed-files/finalizer
binding exposes the exact original `FrontierLedgerOrigin` capability.

Copies cannot create, acquire or dispose originals. Repeated/late creation,
preexisting files, expired work, closed handles, unexpected private read position,
over-bound source length, cancellation, finalizer completion or disposal retire
the optional set. Retiring it closes owned handles without deleting source or
diagnostic files, stopping app processes, or consuming the legacy finalizer slot
merely by creating the origin.

`WasCreatedEmpty` is a historical creation fact, not cursor validation. Ledger ID
and source path are locators, not import capabilities. `CursorHistoryValidated`,
live-proof, full-native-writer-custody and publication flags all remain false.
An origin is optional for the existing disabled helper/legacy test paths; a future
cursor-proof gate must require it explicitly. No production caller is enabled.

## A8b26: original incremental cursor and observations

The original retained creation stream now performs incremental indexing itself.
`TrySynchronize()` accepts no path, bytes, snapshot or success flag. Each operation
captures the current length and reads only previously unread bytes, in 32-KiB
blocks. LF alone completes a physical line; CR-only bytes and incomplete tails
remain uncommitted until LF arrives. Incomplete bytes are hashed once, not reread
on every synchronization. Private line-start offsets are capped at 262144 entries
(2 MiB of offsets), alongside the existing 256-MiB source bound. This does not
replace the processor's separate strict UTF-8 and per-line text bounds.

`TryCaptureInitialObservation()` performs an actual synchronization and freezes
the bookmark/count/byte positions. It is one-shot and requires exactly one bound,
still-live app, before the second launch and before launch sealing/file handoff.
The real scenario must call it at its existing post-helpee-startup bookmark point;
the launch-count guard alone is not a claim of application readiness. Empty and
nonempty initial observations are both supported. Later syncs cannot mutate it.

Live synchronizations stop when source sealing starts. The original finalizer's
one-shot `TryCaptureLedgerFinal()` requires that initial observation, the exact
sealed source, and active original processing work with the source reader still
at byte zero. It consumes only the unread cursor suffix, requires a fully
LF-committed EOF, and compares the cursor's cumulative SHA-256 against the already
computed original source-seal hash. This catches rewrites of previously observed
prefix bytes, even outside native writer ranges, without a cursor rescan or an
additional whole-source hash pass. Only initial and final observations are kept.

Before adopter creation, the original launch lock serializes cursor operations
with app binding/handoff creation. After creation, the existing adopter control
lock serializes live cursor operations with adoption/sealing. Final capture uses
the existing bounded read wrapper, preserving an already-running processing
interval. All use the same cumulative five-second clock. Resource callbacks do
not acquire GUI/finalizer locks; final capture enters the origin resource only
under the original adopter lock. Copies cannot operate or retire originals;
failed/repeated/late originals and stale, truncated, moved-position, closed,
cancelled or expired resources retire the optional set.

These are immutable original byte/index observations, not semantic writer
snapshots or an exact processor-invocation join. `CursorHistoryValidated` and all
approval flags remain false. The disabled helper still accepts its old scalar
bookmark/count, so it is deliberately not upgraded to cursor proof yet. There is
no production call-site change, second authority evaluation, or live-256 rerun.

Verification: `artifacts/frontier-ledger-cursor-20260916/RESULTS.md`.

## A8b27: exact owned cursor/processor join

Review identified a framing mismatch before joining scalar counts: the original
cursor indexes LF bytes, whereas the generic stream processor accepts bare CR as
a line separator. The new internal owned-cursor entry uses an LF-framed, strict
UTF-8 reader. It preserves embedded CR as data, strips one terminal CR from CRLF,
and accepts a UTF-8 BOM only at source start. It rejects incomplete final lines,
invalid/incomplete UTF-8, and over-bound lines. Legacy path and generic stream
readers are unchanged. Each physical line's original byte start is captured
during the same processor pass; no source rescan is added.

The processor freezes initial writer identity/scope/sequence/count facts when
it reaches the actual bookmark, before processing that line (or at EOF for an
end bookmark). The prefix contains at most 64 immutable records. Its strings
are the same immutable strings already bounded/accounted in the final writer
snapshot, not another metadata allowance. Later result-array/counter mutation
cannot change either original snapshot. Strict UTF-8 line decoding explicitly
checks the tail on both Framework/PowerShell 5.1 and modern .NET/PowerShell 7.

`TryProcessObservedLedger` is a separate explicit optional binding operation.
It accepts only semantic-event selection, never bookmark/count, source, path,
snapshot, hash or success flag. The original binding opens its source, starts
the existing cumulative interval, captures original final/recovery observations,
and supplies their actual count/bookmark and derived batch keys to one processor
invocation. It chooses new output destinations under its registered root.
The previous scalar helper remains unchanged and cannot produce cursor proof.

`TryMatchProcessedCursor` requires the exact original source/result/capture,
current immutable validation, native writer match, recovery match, source EOF,
and active processing work. It compares every original byte index, exact
bookmark, source byte/line counts and immutable visit count, then checks the
initial writer set is a monotonic scoped subset of the valid final writer set.
The actual retained source is one fixed active generation from creation through
seal, identified by an original opaque generation ID distinct from ledger ID.
Initial/final observations belong to that same owner/generation and their byte
and line counts cannot regress. Gaps, duplicates, wrong consumed counts and
prior cursor failures cannot produce a successful match; no diagnostic counters
or empty failure-reasons array are imported as approval.

Only the exact current `FrontierCursorMatch` reports `CursorHistoryValidated`.
Its input observations/origin still do not independently assert this join.
Copies/foreign results cannot consume or retire the original attempt. Missing,
wrong, late/repeated or expired original joins retire source/output/payload
resources. The match follows both original observation and writer/recovery
lifetimes after the work interval finishes. All live-proof, full-native-custody
and publication flags remain false. At A8b27, copy/persistence/parent gates were absent;
A8b28 now owns the original copy subgate without enabling production.

Verification: `artifacts/frontier-cursor-match-20260916/RESULTS.md`.

## Next bounded sequence

1. Preserve A8b29's exact frontier-persistence capability alongside the original
   copy/cursor/writer/recovery matches. It does not upgrade legacy ledger JSON.
   See `docs/file-transfer-frontier-finalizer-gates-rca.md` and
   `artifacts/frontier-evidence-persistence-20260916/RESULTS.md`.
2. A8b30 registers the original parent; A8b31 prepares the complete original GUI
   set; A8b32 pulls its native files provisionally through the original GUI process
   handle. A8b33 transfers/joins the complete immutable facts/maps provisionally.
   A8b34 adds exact two-sided acknowledgment; A8b35 binds a separate parent
   escrow/commit to successful original completion and native GUI exit without
   bypassing original provisional cleanup. Request cancellation still closes all
   parent custody. A8c1 transfers the exact set into the original unbound primary
   request and provides private independent-position native readers. It is still
   parent-process custody by itself. A8c2 adds exact native primary-worker
   attachment; A8c3 adds the native source reader and exact emitted-line/physical
   origin ownership. A8c4 binds supplied original arrays/views and one invocation
   pair as a separate native association primitive. A8c5 adds exact pair-owned
   native payload capture, without enabling the offline receipt-JSON resolver.
   A8c6 captures a bounded exact physical witness batch and maps native completion/
   marker ranges through retained original cursor offsets. A8c7 joins the trusted
   primary's original unresolved failure to the native receipt and original
   completion views within that cap, retaining a bounded relevant-field snapshot.
   A8c8 adds bounded original-state revalidation and one local diagnostic
   preparation with native-derived input descriptors. A8c9 adds independent
   parent escrow, bounded exact transport/adoption, original-state revalidation
   at worker commit and one transport take after successful native worker exit.
   Existing worker-input cleanup is preserved. A8c10 owns both original primary
   output pipes from launch and joins their native EOF/capture to that exact
   transport. A8c11 carries that exact join through native source/catalog and
   host-correct output report agreement, the original atomic receipt writer and
   exact returned receipt. It preserves the original terminal status, including
   proof-incomplete results; it does not issue publication completion or route
   authority. A8c12 persists the original packet and bound terminal metadata
   before cleanup as a separate historical diagnostic, without changing status
   or granting authority. A8c13 preserves ordinary reports independently of optional
   native output failure and provides default-off branches in both original
   parents. A8c14 supplies the original pre-analysis count on the native hash
   lease without reopening the path or substituting metadata; the genuine chain
   creates its request from the actual count before transfer. A8c15 composes the
   exact accepted completion, native hash/count, request and transfer under one
   original parent resource owner; controlled headless integration reaches terminal
   persistence for both parent roles. A8c16 supplies native launch readiness and
   a bounded worker wait outside the request mutex before unchanged attachment,
   preserving ordinary reports and the single-launch/attachment limits. A8c17
   supplies post-mutex native offer/adoption wakeups and bounded waits outside
   ownership locks, retaining exact adoption/commit validation and work budgets.
   A8c18 binds the original compact analyzer's full/compact pair and exact
   selected-candidate array to native diagnostics. Complete already-read source
   lines supply both original bridge-startup raw scans without path reopening;
   the original reports, failure and separate probe-subset evaluation are kept.
   A8c19 wires both original retained-analysis parents through exact source
   preparation, one native launch, non-consuming offered-signal checks inside the
   original watchdog, and exact receipt/persistence before cleanup. It preserves
   original status/failure behavior and adds no source or evaluation pass.
   A8c20 verifies budget-rejected preparation, strict pre-launch collision/stale/
   executable failures, post-launch capture/receiver collisions and original
   cleanup/report behavior in both parents. It adds no production fallback or
   retry. Original GUI-launch/ledger/app/finalizer custody delivery still needs
   integration and failure coverage; no production call site enables the branch.
   A8c21 adds the original GUI-parent launch/readiness boundary, with early native
   attachment only after binding/registration, unchanged ordinary reports and
   lock-serialized signal cleanup. A8c22 consumes that owner at actual GUI entry
   and creates the native source through the original append-only initializer.
   Creation succeeded but native custody was rejected is an output-only fact
   that permits ordinary initialization, never origin reconstruction. Native
   handles remain private and cleanup survives scenario-cleanup failure. Actual
   app requests/output, native initial/cursor observations and finalizer/parent
   delivery remain next; ordinary ledger scalars cannot supply those operations.
   Verification: `artifacts/frontier-retained-parent-integration-20260919/RESULTS.md`.
   Failure verification: `artifacts/frontier-parent-failure-containment-20260919/RESULTS.md`.
   GUI readiness verification: `artifacts/frontier-gui-start-20260919/RESULTS.md`.
   GUI entry verification: `artifacts/frontier-gui-ledger-entry-20260919/RESULTS.md`.
   A8c23 adds report-preserving app launch and real report-task EOF waiting in
   explicit original GUI helper branches. No scenario calls them yet. App-side
   readiness/request delivery and original native initial/cursor observations
   must still be integrated before finalizer/parent custody. Verification:
   `artifacts/frontier-app-reports-20260919/RESULTS.md`.
   A8c24 supplies child-only app request locators and a bounded native readiness
   wait after actual binding/report setup. Signals cannot replace original
   attachment checks, and collisions/cancellation preserve ordinary reporting.
   The receiver is not called by real app startup yet; exact app-owned lease,
   catalog/writer and drain/handoff lifetime must precede actual scenario opt-in.
   Verification: `artifacts/frontier-app-start-20260919/RESULTS.md`.
   A8c25 supplies the next scheduling prerequisite: original-request file-ready
   wakeups and bounded waits before file attachment and after submission, without
   consuming attachment/acknowledgment early. Exact native checks remain mandatory.
   Actual app-owned setup must run outside UI startup and retain its writer through
   final drain/handoff cleanup; that integration is still pending. Verification:
   `artifacts/frontier-file-rendezvous-20260919/RESULTS.md`.
   A8c26 adds that default-off background app lifetime, with setup cancellation
   before final drain and exact catalog preparation/markers, original native
   file submission, adoption/acknowledgment and lease completion before cleanup.
   Ordinary drain survives optional rejection. Actual service observation
   admission remains unwired; deterministic headless producer fixtures do not
   constitute live desktop activation. Bind real observations, then connect
   GUI request/cursor/finalizer and parent custody delivery. Verification:
   `artifacts/frontier-app-lifetime-20260919/RESULTS.md`.
   A8c27 binds the desktop service to that exact owner and admits its first
   eligible fallback repair capture outside the service gate, with exact
   post-admission context/data/contract checks and no replacement on rejection.
   Actual capture gates close before final preparation; positive observations
   survive reset/disposal. The next seam is real emitter/completion-writer
   readiness at shutdown, still explicitly settled by the fixtures. Then
   connect original GUI app requests and cursor/finalizer/parent custody.
   Verification: `artifacts/frontier-service-admission-20260920/RESULTS.md`.
   A8c28 adds a shared bounded wait for admitted positive streams' original final
   submission and exact persisted completion receipts before markers and the
   ordinary drain. New shutdown tests use the real worker, not forced drain or
   log-tail settlement. This does not wait for writer-only/non-admitted streams;
   review that next before GUI request/cursor/finalizer/parent integration.
   Verification: `artifacts/frontier-shutdown-readiness-20260920/RESULTS.md`.
   A8c29 fixes creator-owned retirement when inbound/outbound setup creates a
   context but never installs it. Normal terminal retirement was already correct
   and remains unchanged. Bounded app-owned stream enrollment/wait and creation
   fences remain next; no diagnostic finalizer may manufacture stream closure.
   Verification: `artifacts/coordinator-retirement-20260920/RESULTS.md`.
   A8c30 adds bounded app-service enrollment before first stream emission,
   retaining up to 64 original completion tasks across 16 service scopes without
   context retention or slot reuse. Closed creation scopes and one-shot sealing
   fence shutdown; writer-only/retired streams and positive receipts share the
   existing wait budget. Overflow, timeout or late creation reject diagnostics,
   not ordinary transfers/drain. Original native checks retain registry validity.
   GUI request/cursor/finalizer and parent custody delivery remain next; live
   callers stay disabled. Verification:
   `artifacts/frontier-producer-registry-20260920/RESULTS.md`.
   A8c31 threads both original requests and report tasks through an explicit
   launch-only scenario branch and captures the owner's original initial cursor
   between app launches. It leaves ordinary bookmarks unchanged, keeps native
   capabilities private, and retains a started child for cleanup before report
   setup can throw. The live caller remains disconnected; optional setup is
   cancelled before ordinary cleanup until native adoption-before-exit and
   finalizer/parent delivery are integrated. Verification:
   `artifacts/frontier-scenario-launch-20260920/RESULTS.md`.
   A8c32 connects original file-channel readiness and adoption-before-exit in
   that explicit branch. Both close requests precede the native event wait;
   adoption and process-exit waits share the unchanged cleanup budget. Timeout,
   cancellation or incomplete native completion discards optional ownership,
   while ordinary cleanup/report tasks remain intact. The private original owner
   outlives cleanup until finalization returns. Live entry stays disconnected;
   adopted-file/final-cursor and finalizer/parent binding remain next.
   Verification: `artifacts/frontier-cleanup-adoption-20260920/RESULTS.md`.
   A8c33 connects adopted-file sealing and the matching original finalizer binding
   after output EOF, then runs original final cursor/source processing once. Its
   private binding/result cannot be reconstructed from the separate legacy
   snapshot or exported as legacy capture. Optional failure preserves ordinary
   finalization. Native semantic copy/persistence and parent delivery must still
   be integrated before legacy completion ends original ownership. Live entry
   remains disconnected. Verification:
   `artifacts/frontier-finalizer-source-20260920/RESULTS.md`.
   A8c34 connects original semantic copying and diagnostic persistence before
   ordinary finalization. The native copy uses a distinct fixed path, leaving
   the legacy analyzer path unchanged. Full-finalizer tests also require the
   actual catalog to fit the shared bounded selector count (now 2,048); aggregate
   metadata/work/witness bounds remain unchanged. Completion and cancellation
   retire native ownership. Native closure now samples channel ACK after observing
   attachment completion/exit, avoiding an earlier cached state across those
   transitions. Deterministic before/after models cover the read ordering; they
   are separate from native custody tests and do not trace the exact intermittent
   cleanup failure. Further tracing identified a skipped ordinary process wait:
   HasExited was true before the original native handle signaled. Cleanup now
   waits once on those original handles, outside locks and within its unchanged
   shared deadline, before requiring successful native completion. This separate
   gap has a controlled early-HasExited native regression. Parent delivery remains the next integration edge,
   with live entry still disconnected. Verification:
   `artifacts/frontier-finalizer-persistence-20260920/RESULTS.md`.
   A8c35 reproduces a later parent-completion rejection caused by the distinct
   native copy filename. The join now binds both exact fixed native/legacy paths
   to the original artifact root, keeping native identities/content checks and
   legacy encoding unchanged. The primary uses the held native copy, not a legacy
   path reopen. Downstream terminal helpers and retained-analysis entry points now
   require filesystem existence only for ordinary path-based input; native labels
   require original preparation/reader acquisition before use. Parent scheduling,
   single completion selection and custody transfer
   before original request disposal remain required, with live entry disabled.
   Verification: `artifacts/frontier-parent-handoff-20260920/RESULTS.md`.
   A legacy summary cannot recreate that owner.

Verification: `artifacts/frontier-ledger-origin-20260916/RESULTS.md`. No portable
rebuild, live-256 test, matrix, commit or push belongs to this checkpoint.
