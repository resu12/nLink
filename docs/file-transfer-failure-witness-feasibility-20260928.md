# Failure-bearing native witness: feasibility review and first fix plan

Date: 2026-09-28. Source and saved-evidence review only. No code edits, tests,
builds, analyzer/resolver reruns, transfers, acceptance changes, commit or push.

## Decision

A late-publication episode is **compatible with the timing inequalities**, but
an end-to-end failure-bearing test is **not implementation-ready**. Two concrete
input/consumer mismatches must be addressed before a new transfer can establish
the native witness milestone:

1. The normal retained semantic input excludes the native payload marker that
   the witness resolver requires.
2. The latest genuine episode has a watchdog-started stall; the existing
   classifier accepts that shape, but the native resolver accepts only a
   recent-callback-started stall.

The immediate recommended change is the first, deterministic retention edge:
retain the existing marker as non-authority diagnostic evidence, with focused
origin/provenance tests. Do not implement a timing fault merely to reach a
resolver whose required marker is absent. The watchdog compatibility and the
new fault profile remain separate bounded work, not hidden additions to that fix.

## Current evidence, not a new evaluation

Baseline: v0.7.0 at a653d4063070235c71fe211480aeb37863d9b2a4, with the existing
worktree. All 1,407 previous pins and 480 dirty entries matched at entry.

Latest original run:
artifacts/native-diagnostic-custody-implementation-20260928/pwsh-frontier-schedule-985400656c1f4ae1bad8739793a76e25.

Its original summary records raw recent-progress candidates=1, recovered=1,
unresolved=0, first selected proof-chain failure=(none). This is stronger than
saying merely that the resolver did not run: the genuine candidate existed and
was closed by the original analysis. No new correlator or authority evaluation
was used in this review.

The immutable semantic input SHA-256 remains
5165f950b022dd02271f756fa62d022f290a0789574d5b521b0c64225cfd8844.
The original operational log remains
5373bec7e534ace88c0f12b5b75f3d51b0c72eec0eaf12d9b117816ddb84c4ba.

| Semantic line | Original observation |
| --- | --- |
| 938 | Authorized repair progress 0 -> 1, receiver writer sequence 559 |
| 978 | Real frontier_stalled receiver-state deferral, sequence 583 |
| 1052 | Stall root, reason frontier_stall_repair_due, sequence 631 |
| 1057 | Actual recent-grace recheck, stall_grace_armed, sequence 632 |
| 1076 | Ordinary sparse write advances frontier 1 -> 4, sequence 643 |
| 1110 | Original accepted coordinator completion, sequence 656 |
| 1119 | Receiver state for frontier 4, sequence 665 |

The stall starts at 10:40:38.6461634Z and its unchanged deadline is
10:40:39.3961634Z. The write and sent-state writer records precede that deadline.
These are diagnostic log coordinates, not newly issued native witness
capabilities or a replacement UTC/QPC timing evaluation.

## Confirmed missing marker-retention edge

The original operational log contains one marker at physical line 1641,
writer sequence 761, observation c3c52f6ead634047b573afbe6084aa24.
The original semantic input contains zero such markers.

GuiSmokeEvidenceCatalog.ps1:2 defines the explicit non-authority diagnostic
event list; its semantic union at line 61 does not retain
filetransfer_frontier_payload_marker. The native receipt itself still reaches
the primary through its independently validated manifest and payload pins.
This explains how the successful positive-custody test can coexist with a
missing semantic marker.

LocalNativeFrontierWitness.ps1:190 independently searches the original full
analysis rows for exactly one matching marker and later binds its semantic and
physical origins to the original native batch. Zero matching rows cannot satisfy
that contract. The positive test intentionally inspected the native marker
range without invoking this failure-only resolver; it did not verify this edge.

This is a static, source-backed prerequisite defect, not an observed resolver
exception in the saved run. The branch was ineligible and was correctly skipped.
Do not reopen the old custody, edit its retained log, append a reconstructed
marker, or relabel that run as a witness pass.

## Confirmed watchdog compatibility gap

The original stall uses frontier_stall_repair_due. The existing classifier's
Get-FileTransferRecentProgressGraceStallProgressClosure at
tools/FileTransferFrontierGraceHandoff.ps1:1504 allows that watchdog-first root,
but validates the exact current interval, root-before-recheck ordering, matching
highest frontier, unchanged 750 ms deadline, and predeadline recheck. It also
rejects competing roots.

The native producer's Stall/Rechecked/Write lifecycle captures the actual
objects and stamps; it does not require the diagnostic reason to name the
recent callback. In contrast, LocalNativeFrontierWitness.ps1:150 requires
frontier_recent_progress_grace_elapsed unconditionally.

Therefore this saved watchdog shape would encounter native_frontier_stall if it
otherwise became eligible and reached that check. That conditional conclusion
does not invent a selected failure in this successful capture. Simply accepting
both reason strings would be insufficient: any later bounded fix must retain
the exact original generation, source, timing, ordering and competing-root
checks, without a second authority evaluation.

## Timing feasibility and limits

Let P be the original repaired-progress time, S the actual stall root, W the
first subsequent contiguous commit, and C the accepted receiver-state completion.
The existing native timing contract permits:

- S >= P + 1000 ms.
- S <= W < S + 750 ms, with the original UTC/QPC checks.
- S + 750 ms <= C < W + 1000 ms for PublishedWithinObservationBound.

The ordinary recovery closure requires the matching sent-state record strictly
before S + 750 ms. Natural-progress closure applies only through the earlier
recent-grace deadline. Thus a real predeadline write followed by a late accepted
publication can, in principle, produce a positive diagnostic without satisfying
those recovery paths.

This does not guarantee the supported class is the primary's first unresolved
failure. The original classifier also considers successor repair, Tuna handoff,
source supersession and earlier competing failures. Those must remain unchanged;
the original primary must make the selection. The resolver still needs 1-64
exact native witnesses. The saved episode has 16 coordinator rows in its
progress-to-completion interval alone; this is not a total witness-count proof
for a longer delayed episode.

A candidate scheduling design is to release original data frame 1 after the
genuine recheck, around S+250 ms, and delay physical sends of the resulting
receiver-state publication until around S+800 ms. These are proposed test targets,
not revised runtime deadlines or guaranteed real-time performance. Require the
actual completion before W+1000 ms, within the original copy cancellation, and
before the oldest still-held ordinary frame's 3000 ms limit. The saved sparse
data episode already spends much of that three-second hold window before S;
do not extend it to accommodate a late run. Insufficient remaining budget must
fail unavailable before launching the fault.

## Why the current carrier cannot express that fault safely

The current metadata hook observes chunk batches only
(NknSignalingTransport.OfflineFrameMetadata.cs:15); the existing schedule holds
ordinary data frames 1, 2 and 4, not receiver-state publications.
The existing large-bulk one-shot hold cannot identify a receiver-state frame.

Receiver state uses a separate lifecycle path
(NknSignalingTransport.FileTransferTransportChannel.cs:300). It serializes
distinct control and bulk envelopes, submits a control-queue copy plus direct
control and bulk copies, and only then returns the original delivery result.
The normal ReceiverState peer-copy timeout is 750 ms; the 2500 ms durable
checkpoint override is not a general receiver-state allowance. The retry-state
interval is also 750 ms.

Holding only one lane, or holding receipt delivery after SendAsync already
completed, does not establish late accepted publication. A future test seam must
correlate the exact original frame to every original encrypted copy, preserve
bytes, delay real physical writes before taking the pipe write lock, and never
fabricate queue acceptance, peer visibility or coordinator completion.
Unrelated traffic must continue. Existing pre/post-send validators and native
source invalidation remain in force. A competing state publication must stop the
test, not be silently dropped.

Such a seam requires a separately bounded test contract: exact identity and
hash matching, bounded frame/byte count, cancellation/expiry/peer retirement,
one-shot release, default-off behavior, and verification of every successful
send against a real pipe write. No decryption, payload editing, result override,
private state mutation, extra evaluator, or production timing change is justified.

## Smallest next implementation and verification

Implement only marker retention first:

1. Add the existing event to the GUI's non-authority diagnostic retention list,
   not the SnapshotAuthorityV1/GenerationStampedEffects catalogs. Keep the
   pinned LegacyV0 catalog and acceptance predicates unchanged.
2. Add a focused regression proving the default catalog contains the marker and
   does not confer authority. Extend the stream/native-origin test to retain the
   exact original line, writer sequence and physical source index through copy
   and persistence, including duplicate/mismatched marker rejection at its
   existing validator rather than accepting arbitrary marker text as custody.
3. Run the affected catalog/retention tests and existing native marker-range
   controls only. No aggregate suite or network run is needed for this first fix.
   Keep the current no-selected-failure positive profile unchanged.
4. Stop before new fault implementation. Review/verify the watchdog binding with
   exact negative controls, then specify the bounded all-copy delay seam. Only
   after those prerequisites pass should one rebuilt small characterization be
   proposed. A canonical 256 MiB same-pair witness and live enablement remain
   later gates.

This plan does not approve implementation, a transfer rerun, changed witness
eligibility, changed clock bounds, a portable build, live-256 or full matrix.
It narrows the next action to a demonstrated input-retention defect.

## Preservation

Only this report and ignored evidence/seal files are added. Source, tests,
binaries, earlier reports and successful/failed captures remain unchanged.
No tests were rerun because this was source/evidence analysis only.
Evidence and the independent final pin/dirty-state check are in
artifacts/failure-witness-feasibility-20260928/.
