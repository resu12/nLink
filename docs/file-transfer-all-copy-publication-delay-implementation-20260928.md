# All-copy publication delay: bounded primitive implementation

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Outcome and scope

Implemented step 1 of
`file-transfer-all-copy-publication-delay-contract-20260928.md` only.
The default-off scheduling seam, bounded owner, and pipe-write observations are
ready for isolated profile integration. **87 distinct focused checks pass:**
57 primitive/I/O controls, 16 transport controls, and 14 existing regressions.

No original-app characterization, transfer, live NKN check, live-256, GUI matrix,
portable-package rebuild, analyzer rerun, commit, or push. Existing native
captures, primary verdicts, acceptance, timers, and positive/no-selected-failure
profile/probe remain unchanged. This is not evidence of the late native receipt
or the required unresolved failure being selected by the original primary.

## Implementation

- `NknSignalingTransport.OfflinePublicationDelay.cs` adds an internal,
  transport-instance registration scoped to one session/transfer. Only its
  ReceiverState requests reach the binder. Unselected requests bypass metadata
  construction and its profile limits. Metadata contains a copied immutable
  state/range snapshot and exact original control/bulk envelope IDs, lengths and
  hashes, plus scalar source/policy facts. No contexts, validators, authority
  tokens, decrypted/replacement bytes, or result-producing callback are exposed.
  Unsupported state subtypes fail rather than leaking mutable subclass fields.
- The selected queued copy waits before enqueueing. Direct copies wait after
  their unchanged timeout starts and before transport locks. The existing
  per-copy pre-send validator runs once immediately before delayed dispatch;
  initial request validation and post-send validation remain. Normal/null paths
  retain their validation positions, acceptance and copy policy.
- `AttemptOfflinePublicationDelay` binds one actual publication against the
  supplied expected frame/source/peer. It requires the fallback-v6 ReceiverState
  profile, frontier 4/highest 7, nonterminal/unpaused state, bulk destination,
  peer visibility, normal caller cancellation, one peer attempt and 750 ms copy
  timeout. One absolute monotonic release serves all three roles; there is no
  arrival barrier or renewal. Direct waits check the original timeout-start
  budget, including the 100 ms completion allowance.
- Metadata and two encrypted originals plus three serialized copy buffers are
  reserved with existing held data under the same 1 MiB ceiling. Limits include
  eight exact ranges, two descriptors of at most 1 KiB, selected envelopes of at
  most 16 KiB, and three role/write slots. The conservative 64 KiB reserve also
  covers bounded state/metadata objects and source serialization; derived V6
  fields are included in sizing, not omitted by base-type JSON serialization.
- Cancellation, expiry, source/peer retirement supplied by the owner, competing
  publication, invalid metadata, callback failure, role failure, or write
  mismatch cancels pending work. Selected cancellation stays linked through
  actual dispatch. Disposal awaits started copy completions and the release
  task, including the queued child; concurrent disposal shares that cleanup.
  It clears state/metadata/write records and does not release on expiry.
- `AttemptPipeFrameWriter` extracts the existing header/hint/payload/flush/peer
  check sequence for focused I/O testing. `AttemptNknPipeClient` retains its
  writer semaphore and fatal partial-frame disposal. Its optional nonblocking
  observer sees immutable attempt/completion facts, never payload arrays.
  Completion is observed only after flush and peer check. The bounded owner
  requires two physical control completions and one bulk completion, one call
  per lifecycle role, and the unchanged successful original copy results.

`DrainChildrenOnCompletion=false`, queue priorities, transport selection,
original bytes, retry behavior, timeout values and coordinator authority are
unchanged. In particular, a lifecycle bounded-wait failure does not mean the
underlying queued task has retired. The regression deliberately blocks unrelated
traffic, observes that still-pending child, and proves owner disposal waits for
its cancellation to be processed without a selected late write.

## Verification and retained development failures

Evidence: `artifacts/all-copy-publication-delay-implementation-20260928/`.

- `focused-reviewed.trx`: all 57 primitive/I/O tests pass. Its 15 transport
  cases failed in fixture join setup, before registration, because the first
  unique-address variant used the same hexadecimal tail for both fake peers.
  The unchanged address-equivalence check correctly rejected self-source input.
- `transport-final.trx`: all 16 transport cases pass after fixing fixture
  addresses, including an added unsupported-mutable-subtype control. Unique
  non-public-key-tailed addresses avoid both peer aliasing and invite throttling.
- `affected-regressions.trx`: 14/14 pass (ordinary frame metadata; control-plane
  key re-derivation and late-authority rejection; lifecycle bulk redundancy;
  session-end cancellation and peer-visible acceptance).
- The earlier `focused-first.trx` was 58/59: reused fixture addresses triggered
  invite throttling before the hook was installed. The isolated queue test
  passed in `queue-isolation-recheck.trx`. Relevant original log lines are
  retained in `fixture-failure-log.txt`; neither fixture failure justified a
  runtime change or a retry of an original-app characterization.
- One development build used a nonexistent enum name in a new test; it was
  corrected to the existing `FrontierRequest`. Build logs are retained.
  `build-handoff.log` confirms the final Debug build succeeds. Existing project
  warnings remain; the newly added observer is a default-null property to avoid
  an unused-field warning. The final property-only edit changes no tested logic.
- Tests use fake NKN sessions and real local named-pipe writes for transport
  controls, plus injected stream failures for header/hint/payload/flush stages.
  They do not claim original native process ownership or receipt proof.
- Builds used invocation-only `-p:UsedAvaloniaProducts=`; no build configuration
  was edited. No aggregate contract/analyzer/acceptance suite was run.

## Preservation and next step

Entry: all 1,701 prior pins and 493 dirty entries matched. Before-edit copies of
the three existing edited files are retained. Changes are limited to those
files, five new source/test files, this report, and ignored build/evidence
outputs. The final independent audit records exact hashes and intentional
replacements; unrelated dirty files and saved evidence remain preserved.

Next: implement only the separately named small failure-bearing profile and
probe with focused guard tests. Wire original native selection/currentness and
peer retirement to this owner, compute the earliest original publication/data
hold deadline and measured aggregate budget, preserve the existing 3000 ms hold
clocks, and check the additional 100 ms data-hold reserve before arming. Preserve
the positive/no-failure profile. Verify original primary output custody around
its one optional resolver call and cleanup/readiness before proposing a rebuilt
small characterization. No native run is authorized by this handoff; live-256
and the full GUI matrix remain separate gates.
