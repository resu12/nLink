# DevLocal metadata queue race: RCA, fix, and verification

2026-09-26, branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-owned-source-validation-20260926.md`.

## Outcome

Fixed the demonstrated initial data-session queue race. All 18 final focused
and directly affected ownership tests pass. The targeted PowerShell 7 offline
256 MiB rerun now completes the actual transfer and native destination hash
checks. The whole test remains **failed** at the separate, unchanged
five-second original-log custody-processing budget (5002 ms). No acceptance
rule or timeout is relaxed, and no live-test readiness is claimed.

## Root cause and evidence

The previous isolated failure is retained in
`artifacts/frontier-cross-process-receive-20260926/pwsh-writer-source-read-cross-process-valid-f71557581e4042c6be79670e71ee038a/`.
Its `live-operational-log/nlink.log` records:

- Line 116: V4 manifest sent.
- Line 120: DevLocal dispatches `filetransfer.manifest.v4` to a data queue.
- Line 125: the V4 receiver starts.
- Line 150: inbound terminal failure `metadata_not_provided`, zero payload
  bytes/chunks, after the metadata wait expires.

`DevLocalTransport.OpenFileTransferDataSessionAsync` and
`DeliverFileTransferDataFrame` previously both used an unsynchronized
`Dictionary` with separate lookup, allocation, and indexer assignment. Each
path could observe no queue, create its own queue, and replace the other's
published entry. Depending on ordering, either the receiver held an orphaned
empty queue or the manifest had already entered a queue the receiver would
never read. Transport dispatch alone did not establish receiver consumption.

The new regression forces both orders through a one-shot comparer callback:
the other path publishes after the outer lookup selected its old collision
chain but before the outer path publishes. This is a deterministic schedule
of the competing lookup/publication operations, without sleeps or production
test hooks. The test comparer is only an injection seam; real failures do not
require colliding hashes. Both cases fail against the old source at
`Assert.Same`: the competing queue has generation 2 while the returned/current
queue has generation 3. A separate multithreaded case exercises ordinary
concurrent opening/delivery.

The historical process logs do not contain both queue object identities, so
their exact scheduling cannot be reconstructed. The deterministic reproduction
establishes the code defect; successful metadata and transfer completion after
the narrow fix provide the targeted integration check for the observed failure.

## Surgical fix

Use `ConcurrentDictionary` and `GetOrAdd` at both creation sites, so all
participants use the single published winning data-session instance. Existing
route-transition lookups are now safe while initial publication occurs.

Keep session-ID validation after selecting the winner. A mismatched open still
throws, and a mismatched incoming frame is still rejected without delivery.
Cancellation/disposal checks before opening remain. No transport dictionary
grants runtime ownership: coordinator-issued data-session generation, route,
and transition checks are unchanged. Concurrent factories can allocate unused
candidate queues and skip generation numbers; no unused candidate is returned,
published, or delivered to. Generation identity remains unique, not contiguous.

Only `src/nLink.Infra.DevLocal/DevLocalTransport.cs` changes in production
(12 insertions, 17 deletions). No Core/NKN runtime, payload sizing, retry,
logging policy, transfer wait, or retained-log validation cadence changes.

## Verification

TRX results are under `artifacts/devlocal-metadata-queue-20260926/results/`:

- `queue-race-before.trx`: both deterministic cases fail on queue identity.
- `queue-race-after.trx`: the same two cases pass with the production fix.
- `queue-and-ownership-final.trx`: 18 pass, 0 fail, 0 skip. Eight new queue
  regressions cover both interleavings, both sequential arrival orders,
  concurrent opening/delivery with all 16 frames retained, cross-session
  rejection, canceled open, and disposed open. Ten existing DevLocal ownership
  cases cover stale/missing data sessions, exact dispositions, supersession,
  successor rebinding, and invalid fallback bootstrap commands.
- `pwsh-full-size-after-queue-fix.trx`: one failed end-to-end test, now at the
  later retained-log processing boundary rather than metadata delivery.

The full-size artifact is
`artifacts/frontier-cross-process-receive-20260926/pwsh-writer-source-read-cross-process-valid-9043d3e65e244b948e85673b9ce5220c/`.
Both original processes report 268435456 bytes and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
The helper independently verifies the original retained native destination;
the sender verifies source consumption. The resulting 40814298-byte,
54648-line log reaches `stream_source_not_current` at 5002 ms cumulative work.
Successful internal assertions are not counted as an end-to-end pass.

This is the explicit offline DevLocal process fixture, not a live GUI/NKN run.
The targeted test is rebuilt through `dotnet test` before the rerun. No portable
package rebuild, full matrix, commit, or push occurs. Build and whitespace
checks pass.

## Preservation and next step

All 415 current checkpoint pins and the older non-overridden pins matched
before work. All 277 pre-existing dirty files retain their exact bytes/status.
The previously clean DevLocal source, one new test file, and this report bring
the dirty inventory to 280. Recovery hashes and outcomes are recorded in
`artifacts/devlocal-metadata-queue-20260926/FINAL-VERIFICATION.json`.

The first unresolved edge is again original-log custody processing within the
five-second cumulative budget. The previous measured 275734 ownership checks
for 1248 physical reads remain the relevant evidence. Next, review a bounded
validation-granularity contract before changing that cadence: retain full
native checks at source-read and result-publication boundaries, retain
per-record deadline/cancellation checks, and reject all tentative outputs upon
invalidation. That is a review proposal, **not an implemented or approved
contract change**. Do not rerun unrelated suites or a live matrix for this edge.

The outstanding GUI/parent destination-evidence handoff and original writer
join remain required. Route-cycle acceptance still requires a second
coordinator-published `file_tuna_v4`; offline transfer/hash completion cannot
substitute for it.
