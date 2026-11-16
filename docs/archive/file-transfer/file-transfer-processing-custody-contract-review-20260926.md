# Processing custody-check contract review — 2026-09-26

Status: **review only; implementation and changed observation granularity are
not approved by this document.** No runtime, test, timeout or acceptance code
is changed. This continues `file-transfer-windows-custody-budget-20260926.md`
on `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.

## Recommendation

Approve, separately, a processing-phase-only flattened implementation of one
original `Finalizer.OwnsFrontierBinding` check. Within that single synchronous
invocation, replace recursive repetition of the same shared custody predicates
with two fresh, bracketing evaluations and the existing retained-file scan.
Keep every caller's full-check invocation, including both sides of every
source read. Do not introduce a reusable validation token, ambient cache,
time-to-live, or a flag permitting a caller to skip validation.

This is narrower than caching custody for a read, parser batch, processing
interval or publication transaction. Those wider designs are rejected for
this step. The existing cheap buffered-work guard is not expanded into a
native-read or publication authority.

## Current evidence and exact call expansion

The previous checkpoint verifies 611 pins, SHA-256
`1017ec7e95670343c230c62e1ad08d0fa59cff85d41dec2d0cfe694eb6c9ddc8`.
The final original-process offline run has two PowerShell 7 passes and two
Windows PowerShell failures. Windows preparation expires at 5001–5002 ms
before acknowledgment. The foreign-writer control has not reached its intended
rejection. Those outcomes remain unchanged; no test is rerun in this review.

Let **C** mean one evaluation of the shared leaf set: parent/closed signals,
GUI request mapping, app requests, file channels, ledger origin and parent
registration. The launch-owner check and file-handoff check both visit C;
their additional identity, sealed-state, deadline and phase predicates are
not interchangeable and must also be preserved.

On the successful, original, sealed path:

| Current operation | Expansion in C evaluations |
| --- | ---: |
| `FinalSealScopeCurrent` | 2: file-handoff scope before and after terminal app checks |
| Adopter `Current` after sealing | 3: current callback, plus the two in the seal callback |
| Adopter `IsRetained` / `IsSourceVerified` | 6: `Current` before and after the retained-file scan |
| `OwnsSealedFiles` | 9: launch owner (1), final seal scope (2), verified adoption (6) |
| `OwnsFrontierBinding` / original reader `CanRead` | 10: frontier scope's launch owner (1), sealed files (9) |
| Original `FrontierSourceRead.Read` | 32: binding before/after (20), adopter before/after (12) |
| One cursor-parser refill, including its outer checks | 52: outer readability checks (20), original read (32) |

Each C checks both file channels. Thus the last row means 104 file-channel
`IsCurrent` calls on that successful refill path, not 104 physical reads.
These are source-derived counts, not fresh runtime counter results. Failure
short-circuits, constructor/EOF/capture work and other callers differ. The
previous 188792 measured channel checks cover a longer interval and cannot
be divided into a definitive refill count.

The proposed single-check change would make the binding row 2, leaving the
adopter's standalone row at 6. The refill expansion would then be
`4 * 2 + 2 * 6 = 20` C evaluations, rather than 52. This is a testable structural
target, not a promise of a particular speedup or of meeting the full budget.

Relevant source anchors:

- `LocalGuiWriterFrontierFinalizer.cs`: `FrontierScopeCurrent` (37),
  `OwnsFrontierBinding` (49).
- `LocalGuiWriterFrontierSeal.cs`: `FinalSealScopeCurrent` (33),
  `OwnsSealedFiles` (51).
- `LocalFrontierFileAdoption.cs`: `IsRetained` (47), `Current` (151).
- `LocalFrontierFileSeal.cs`: `IsSourceVerified` (35),
  `TryReadSealedSource` (52), `BufferedWorkCurrent` (107).
- `LocalGuiWriterFrontierSource.cs`: `CanRead` (46), `Read` (84).
- `GuiSmokeLogLedgerStreamingProcessor.cs`: `CheckCurrent` (401),
  `CheckOwnedBoundary` (437), cursor native read (512), final return check (1237).

All except the processor are in `tools/FileTransferOps`; the processor is in
`tools`. The recovery checkpoint pins these reviewed sources.

## Proposed bounded contract

### 1. Eligibility and lifetime

The entry point remains the exact original finalizer's check of its exact
remembered binding. The specialized branch is eligible only for its original
sealed files and adopter, exact issued reader, verified source, active one-shot
processing phase, and already-running original cumulative clock. Both original
app contributions must still be held. Parent handoff/registration must not have
started; the parent channel must not be attached.

Determine eligibility from original private relationships under their existing
locks, not from caller labels, a supplied boolean, an exported reader handle,
or a completion DTO. Copied/foreign binding or reader objects must retain their
existing rejection behavior without consuming or disposing originals.

A legitimate non-processing phase uses the unchanged legacy traversal. A
failed validation in an eligible processing phase is terminal for that check:
it must never retry through the legacy branch to turn failure into success.
A phase change during evaluation fails the specialized check; do not carry a
result across `FinishProcessingWork`, parent attachment, or disposal.

### 2. One flattened check, no carried verdict

Within the eligible invocation:

1. Check exact finalizer/binding/owner/files/adopter/reader relationships,
   local retirement/processing/cancellation state, original running clock and
   deadline, and the existing strict finalizer request stage/empty completion
   fields. Preserve the full registration/run/scenario/root/native-incarnation
   matching already required by the ownership chain.
2. Evaluate C freshly, including the additional sealed-registry and existing
   handoff-deadline predicates; check both original apps' terminal closure.
3. Validate every retained contribution file against its existing role-specific
   manifest entry, and the original sealed source against both manifests and
   its sealed length. Preserve the existing handle validity, native file type,
   identity, attributes and size predicates. Do not deduplicate distinct roles
   merely because they refer to the same native file.
4. Evaluate C again from current native resources, and recheck terminal closure,
   exact ownership/phase relationships, finalizer request fields, cancellation,
   retirement and cumulative deadline before returning.

The existing terminal closure requirements include original request/launch
membership, acknowledged file state, completed native attachment, signaled
original process handles, zero exit codes and drained original output.
C retains complete mapped registration/digest/body/reserved-byte comparisons,
state monotonicity, submission and ACK identities, cancellation and original
ledger resource checks. Existing leaf validators should be reused, not copied
into a second subtly different implementation. Do not add downstream semantic
writer-join predicates here: they remain separate gates, not custody authority.

The retained-file scan is still an actual scan, not a cached result. It is
bracketed by fresh shared-scope checks as it is today. No new payload/source
hash traversal is implied; the original sealing and consumption hash checks
remain in their current operations. No files are reopened by pathname.

Any resource-only helper extracted from the adopter has no standalone custody
meaning. Its boolean is consumed only by this original composed check and by
the unchanged adopter path; it cannot mint an owner, capture or completion.
No new public constructor, setter, skip-validation flag, general callback hook,
thread-static state, `AsyncLocal`, reusable receipt or stored success bit.

### 3. Checks that remain at their current call sites

- Parser/cursor checks before and after every existing refill/native read;
  the original reader's before/after read checks and the adopter's standalone
  validation; exact 32768-byte cap, position tracking and EOF probe.
- Full checks at completed parsing, before/after output sealing, capture
  construction and final return, including output-capture currentness.
- The approved buffered-record deadline/cancellation checks. Generic streams
  and wrappers keep their existing outer validation behavior; none can select
  the specialized branch. A wrapper that delegates to the same original reader
  may naturally encounter its optimized inner check, but cannot suppress its
  own checks or obtain a different ownership grant.
- All standalone adopter and cursor-resource callbacks, including those during
  processing, and all non-processing source/facts joins, semantic copy, evidence
  persistence, parent files/facts, acknowledgment and completion gates. Do not
  optimize these through an earlier processing result.
- Existing local parser, GUI cumulative and parent cumulative five-second
  clocks. No replacement, reset, premature stop or moving work outside them.

### 4. Synchronization, failure and the detection tradeoff

Preserve the existing finalizer -> launch-owner -> adopter lock direction.
Do not acquire the binding's `sourceSync` from inside that chain. Resource-only
helpers must not call the reader, binding or owner currentness getters while
holding the adopter lock. In particular, the existing origin/cursor callbacks
can enter the adopter from an origin lock: this proposal must not redirect them
through the finalizer or acquire a new reverse lock edge.

No new lock is held across a source read, output I/O, interprocess wait, user
callback, asynchronous suspension or parent publication. Selection, resource
scan and final guards must handle concurrent phase change/disposal explicitly;
an entry-time phase observation is insufficient. Reuse existing failure cleanup
after releasing resource-only helper locks. Faults and disposed native handles
fail closed; copied objects still cannot retire original owners.

This is **not exactly the same observation schedule**. Ten repeated shared
samples within one binding check become two. A persistent change to a predicate
in C after its opening observation but before its closing observation is
rejected by the latter. A later change can only be caught by a subsequent
existing check. The retained-handle scan is separate: a handle lost after that
scan is detected when its own validator next runs, not by a C predicate that
does not inspect it. A transient change restored between samples may go
unobserved, including a temporary mapped-state regression not seen by a leaf
validator. There is no atomic cross-process snapshot or continuous-ownership
guarantee, either before or after this change. Locks do not make externally
writable mappings immutable.

A detected original-owner validation failure is sticky for the invocation even
if a test restores the bytes later. It must yield no successful read/result/
capture/publication from that operation, and must retire the original source
and tentative outputs by their existing cleanup path. A post-read failure need
not erase caller buffer bytes, but must not return a successful byte count or
let the processor consume them. A final observation cannot guarantee absence of a later race; existing
returned-capability checks remain required. The proposal requires explicit
approval of this narrower detection-granularity tradeoff.

## Implementation boundary and verification gate

The candidate production edit surface is the original frontier finalizer,
sealed-file ownership composition and adopter resource-only helper/phase
checks. Factor existing predicates surgically; do not rewrite the parser,
source reader's call sites, channel codecs, transport, or parent protocol.
If implementing this requires a new reverse lock, broad public API, skipped
outer boundary or weakening a listed leaf predicate, stop and revise the
contract before expanding scope. Do not assume this review proves the design
can be implemented without such a conflict.

After separate approval, use opt-in test-only hooks on both PowerShell hosts:

| Test group | Required proof |
| --- | --- |
| Structural count | One successful eligible binding check evaluates C twice; no verdict survives a second call. Confirm the original expansion before changing it. Do not assert speed with a flaky elapsed-time threshold. |
| Original ownership | Copied/foreign finalizer, binding, files, adopter and reader cannot enter, consume, dispose or reissue original custody. |
| Entry and closing samples | Inject persistent GUI/app/channel mapping mutations, digest/body/tail/reserved-byte corruption, state regression, cancellation, parent exit and ledger-resource loss before each sample and between them. Every injected fault must reach the intended hook and reject. |
| Retained native resources | Close or replace each retained/sealed handle in the controlled fixture, alter expected native identity/length, and revoke original output drainage. No path fallback or partial successful result. |
| Work and phase | Expire the same cumulative/local clocks; cancel during the scan; race finish/disposal with entry and closing checks. Reject an eligible failure without legacy fallback, deadlock or leaked handles. Before-begin, after-finish and parent-attached calls retain legacy behavior. |
| Boundary coverage | Fault before/after read, EOF, output sealing, capture construction and return; require no successful result and verify independent handle cleanup. Preserve generic-wrapper and existing buffered-work controls. |
| Publication exclusion | Confirm no specialized path runs after processing ends or parent registration attaches; native parent failure/ACK/completion rules and default-loader authority surface remain unchanged. |

Then run only directly affected original-binding/source/adoption/buffered-reader
tests and selected handoff/parent facts exclusions, with at most one relevant
broader compatibility lane after stabilization. Finally rerun Windows
`writer-valid` and `writer-foreign`, plus PowerShell 7 cross-host compatibility,
through the original offline 256 MiB process tree. A negative control must reach
its injected foreign-origin assertion; a budget rejection is not a pass.

Success requires the complete existing native payload/writer association and
cleanup on both hosts within the unchanged clocks. If the first missing edge
advances, record it without broadening this implementation. No full matrix or
live test yet. Live route-cycle acceptance still requires a second
coordinator-published `file_tuna_v4`; Regular/Tuna protocol 4 and post-Tuna
fallback protocol 6 remain unchanged, with active Tuna protocol 6 invalid.

## Review result and approval request

The bounded proposal is recommended for implementation **only after approval**
of the observation tradeoff and the scope above. The broad cross-call cache
alternative is not recommended. Current Windows failures are not waived.
Wrapper-to-parent payload adoption, primary/analyzer consumption and live
validation remain later milestones. No commit or push is requested here.

This review changes one document only. All 306 pre-existing dirty files and
all 611 prior checkpoint pins remain intact. Its source inventory and recovery
verification are recorded in
`artifacts/processing-custody-review-20260926/FINAL-VERIFICATION.json`.
