# Canonical tail native run: first-edge RCA and bounded fix plan

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Result: **one offline full-size attempt failed**. No retry, live NKN, desktop
GUI matrix, acceptance relaxation, commit or push occurred.

## Execution gate

The prior composition checkpoint matched all 4,223 pins and 551 dirty entries.
The temporary dispatch block was replaced by its already reviewed exact-profile
guard; incomplete composites still reject before launch. A dedicated test uses
the original worker/handoffs and 600-second outer timeout. The Debug rebuild and
10 directly affected admission/exclusion checks passed. The earlier 59 focused
composition checks were verified from their pinned results, not rerun.

Both escalated preflights passed: 4,225 frozen hashes, 552 dirty entries, no
conflicting app/test processes, Debug overrides allowed, developer mode false,
high-resolution clock and over 248 GB free. Inherited `NLINK_*` settings were
cleared only in the runner process; no secret values or user/machine settings
were read or changed. `attempt-started.json` uses create-new semantics.

The single selected test was
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`.
It failed after **209.1820674 seconds**. This is a 256 MiB named-pipe offline
carrier using the real transfer services, not a live network or desktop GUI test.
Original GUI/route-cycle labels in fixture diagnostics do not establish either.

Evidence root: `artifacts/canonical-tail-native-characterization-20260929/`.
Original run: `pwsh-dc4fc373089b49aeac0d9ed4758e149d/` below that root.

## First missing edge

The receiver failed in `ordinary-frontier-tail-highest-final` with
`offline_schedule_frontier_advanced_before_sparse_shape`. That inner error name
is misleading: `MatchesSparseShape` throws whenever `next != first`, including
when the receiver is still **behind** the required frontier.

Current-run evidence:

- Sender output line 6368 records one arming at byte offset 268,219,392, with
  first tail chunk 12,476.
- Sender lines 6371–6388 describe eight unique single-chunk, epoch-zero frames
  at 12,476–12,483. Receiver lines 6415–6429 match every byte count and envelope
  SHA, and record the intended drop/hold/hold/pass/hold/pass/pass/pass actions.
  The last encrypted envelope is 1,441 bytes; the others are 21,921 bytes.
- Carrier receipt does not mean application commit. `AttemptNknPipeClient`
  passes completed pipe frames to the original message event; the schedule's
  `seen` array records physical receipt before application processing catches up.
- The operational log's last receiver sparse commit, physical line 100326,
  reports `next_chunk_index=12002`, `highest_received_chunk_index=12001`, writer
  sequence 41725. This is contiguous prefix progress, still 474 chunks below
  the required frontier. Terminal line 100482 retains 258,091,008 bytes / 12,002
  chunks; it does not show an advanced tail frontier.
- A deterministic invocation of the current helper with `(true,12002,12001)`
  reproduces that same exception. Exact tail `(true,12476,12483)` returns true;
  a prefix gap and an advanced frontier still reject. These four synthetic
  value checks are not a second transfer or a native snapshot.

The first demonstrated defect is the test helper conflating an unfinished,
contiguous application prefix with a violated tail geometry. The exact values
read by the failing predicate were not captured; the saved last commit is not
represented as that exact snapshot. The trace and deterministic reproduction
support prefix-drain rejection, not a production route-authority defect.

The same-pair primary never ran. There is no retained-analysis receipt, native
same-pair observation, completed full-file pair, successful late publication or
live acceptance proof. Do not assess later resolver/budget gates from this run.

## Downstream failure and cleanup

Both native peers failed; the GUI fixture later reported `Original Core writer
shutdown incomplete`. The wrapper's pending payload connection canceled after
about 202 seconds, and the parent reported that retained analysis was not
started. These are downstream consequences, not the first geometry failure.
Both original writer-drain markers remain at physical lines 100494 and 100506.
Post-drain write-failure diagnostics are preserved, not silently discarded.

Independent escalated cleanup inspection found no remaining app/test/native
peer processes (only an idle build server). The sole retained payload is the
268,435,456-byte source; exclusive read succeeds. No destination `.bin`/`.part`
remains, and no cleanup success for a completed transfer is claimed.

## Recommended bounded fix

1. Add a focused regression for a canonical ordered prefix still draining:
   `next < B` and `highest == next - 1`. It must wait, not succeed or be called
   an advanced frontier. Retain negatives for prefix gaps, advanced frontiers,
   wrong final highest, replaced/bound/closed observations and the small profile.
2. Change only the test-only canonical pre-tail readiness predicate to return
   not-ready for that contiguous-prefix state. Keep the original read gate,
   observation checks, cancellation, eight-second edge wait, already-running
   180-second carrier deadline and four-second held-frame expiries. Do not
   clear an observation, release a selected frame early, restart a clock, retime
   repair or turn physical receipt into coordinator authority.
3. After focused checks and review, freeze fresh inputs and separately gate one
   rebuilt offline attempt. Preserve this run and its one-run marker; no reuse
   or automatic retry. If the prefix cannot drain inside the existing budgets,
   retain that new failure rather than increasing them.

This fix plan is not implemented by the characterization step. Production source,
production DLLs, route acceptance, all timing/budget values and authority flags
remain unchanged. The only source changes are the exact-profile dispatch gate,
its negative-test expectation, and the new full-size integration test. Evidence
and final seals preserve the previous work and original failed run.
