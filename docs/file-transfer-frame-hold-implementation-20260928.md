# Bounded offline frame-hold implementation and first failing continuation

Date: 2026-09-28. Branch v0.7.0, HEAD a653d4063070235c71fe211480aeb37863d9b2a4
plus the existing worktree. This implements the first test-only slice from
file-transfer-same-transfer-witness-contract-20260928.md.

## Outcome

The one-shot opaque delivery gate is implemented and its eight focused tests
pass. The single small-pair characterization **failed**. It did produce
coordinator-issued, continuity-stamped frontier repair and actual repair
progress on that small transfer. It did not complete or establish native
receipt/witness custody.

The next boundary is continuation after paired-tail yield while data remains
missing. Do not run the held-256-MiB profile or live-256 yet. No production
Core/Infra, analyzer, acceptance, timing budget, live dispatch or GUI behavior
was changed. No commit or push was made.

## Implementation

- Support/AttemptNknFrameHold.cs holds the first Regular bulk envelope of at
  least 4,096 bytes, once only. It preserves an exact byte copy, reports its
  byte count/hash, caps frames at the existing 1 MiB bound and fails the pending
  send on cancellation or a three-second hold timeout.
- Support/AttemptNknPipeClient.cs opts in only when its test-only OutboundHold
  is supplied. The hold runs before the physical write lock, uses linked
  caller/client-lifetime cancellation, and retains the existing peer check
  before actual write. Foreign destinations are rejected before selection.
  No held send is returned as successful before its bytes are written.
- FrontierAttemptNknCarrierTests.cs adds the separate frontier-hold profile and
  named small/full characterization tests. The gate is armed only after both
  real app services publish fallback, then released two seconds after selection.
  Production timers and the existing eight-second small completion/route-edge
  deadline are unchanged.
- FrontierAttemptCrossProcessReceiveTests.cs passes the explicit profile flag.
  Existing profiles default to no hold. The unrun full-size entry point remains
  an unmet prerequisite test, not an accepted positive.

The test gate sees only envelope kind, lane and length. Chunk identity below
comes from the original production diagnostics, not gate decoding or mutation.
There is no seeded coordinator context, copied receipt, private repair dispatch,
clock edit, fabricated marker or additional authority evaluation.

## Verification

1. An initial compile caught an internal-enum/public-test-signature mismatch.
   The test parameter was corrected to an integer; no production change.
2. Focused AttemptNknFrameHoldTests: **8 passed**, including untouched bypass,
   exact held bytes despite caller mutation, later-frame overtaking, no early
   completion, one-shot release, cancellation, timeout and capacity rejection.
3. Updated harness build: **0 errors**, 338 warnings. No new gate-file warning
   was observed; the build retains existing broader-project warnings.
4. Exactly one escalated offline small-pair run:
   OriginalApps_CharacterizeFrontierHoldOnSmallPair, **failed** in about 94 seconds.
   The named-pipe carrier uses no live NKN connection or wallet.
5. No repeat characterization, full-size transfer, retained analyzer run,
   portable build, aggregate lane or full GUI matrix followed the failure.

Artifacts:
artifacts/frame-hold-implementation-20260928/pwsh-frontier-hold-a8760a38fc81498d9b18a3337df25e81.
The two TRX files and build.log are in its parent directory.

This small profile creates the existing protected 256 MiB harness source, but
the runtime transfer is only 65,536 bytes from its small in-memory fixture.
It does **not** prove the original held 256 MiB transfer. No small-transfer
identity or evidence is imported into the full-size profile.

## First-edge evidence

Original operational log: 1,591 physical lines, SHA-256
1c46a825de2d580951bce4eeed29547dc9e8e8c00d95cd31a2ee748eea8c1260.
These are read-only diagnostic coordinates, not primary-owned witness selections.

| Line | Observation |
| --- | --- |
| 960 / 974 / 979 | A batch covers chunks 0-2; the later chunk-3 frame overtakes it and reaches the sparse receiver. |
| 1018 | Inbound coordinator repair authority for frontier 0, with checkpoint continuity receipt fte1-7229a053a206747e8baeb23c. |
| 1173 / 1176 | The delayed original epoch-0, three-chunk batch is received, then rejected: fallback_frontier_protocol_alias_missing. |
| 1210 / 1212 | Authorized epoch-1 repair settles ProgressObserved and advances contiguous frontier 0 -> 1 using that same continuity receipt. |
| 1252 | Receiver state publishes contiguous frontier 1. No terminal completion follows. |
| 1383 / 1385 | Tail pairing is accepted and paired yield is granted. |
| 1388 | Runtime unlock is admitted as fallback_quiescent. This is not proof that missing data completed. |
| 1391 | Further frontier_stall_repair_due update is suppressed with fallback_tail_retired_owner_exact_scope. |
| 1392 | Recent-progress wake at frontier 1 is stale with route_scope_changed. |

Sender gate output records one release: 64,937 bytes, SHA-256
1132680a9333993d38129966be416d25f27cc34721f6755ebc802b0da3170606.
The later frame overtaking the held batch confirms that the physical writer
lock/read loop was not held behind the fault.

The sender then reaches its existing small completion deadline
(FrontierAttemptNknCarrierTests.cs:284); the receiver never reaches destination
rename/finalization and is cancelled at its existing bound (line 290).
The wrapper reports original Core writer shutdown incomplete, exits 1 and
does not start retained analysis. Its finalization state says timedOut=false:
the outer process deadline did not fire. Subsequent writer_completed diagnostics
occur during teardown; they do not precede the first missing continuation.

Native admission/closed receipt was not established: the read-only in-memory
observation summary is after successful completion and was never reached.
There are zero payload markers. Do not claim a positive native lifecycle merely
because repair progress appeared.

## Bounded RCA / next fix-plan boundary

The capture locates a missing authorized continuation; it does not yet identify
the exact defective predicate.

- The alias rejection is consistent with the strict repair-contract check in
  SessionFileTransferService.FallbackFrontierAuthority.cs:567. Do not admit the
  delayed batch by weakening that check.
- FileTransferCoordinator.cs:12121 deliberately suppresses an exact retired
  owner. Do not resume that owner or let a stale wake issue repair.
- The recent-grace scope check in PullTransferSessionV6.cs compares many fields
  before assigning route_scope_changed. This log alone does not name which
  field changed; it must not be labeled a timer bug.
- Next, trace the original coordinator state around pairing/yield and frontier
  1: identify why unfinished sparse data has neither a current repair owner nor
  an authorized successor. Distinguish intended handoff behavior from missing
  continuation before proposing a production fix.
- Require a focused coordinator/service regression for the exact ownership
  transition. Only then change that first edge if warranted, retaining strict
  stale-callback behavior, and rerun only this small profile.
- The 256 MiB profile remains conditional on that small correctness gate;
  native producer, primary-selected witness and live route-cycle gates remain
  separate. No timeout/cadence/performance tuning or random delay sweep.

## Preservation and cleanup

At entry all 1,137 pins and 455 dirty entries matched the preceding seal.
Only the three listed existing test files were edited; two focused test/support
files and this report were added. The smoke-test DLL was rebuilt. Other prior
work is preserved and checked in the final audit.

GUI PID 40828 and native PIDs 55908/48028 are no longer running. The original
harness source can be opened exclusively read/write; no completion or matching
destination SHA is claimed. No artifact was deleted to obtain a passing state.
Evidence, final pins and the preservation audit are recorded alongside the TRX files.

