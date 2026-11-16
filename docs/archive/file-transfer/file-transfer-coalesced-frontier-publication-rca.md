# Coalesced frontier publication — 2026-09-13

## Latest evidence and first missing join

This RCA uses live run `cbf6bd8a-835b-4f28-868f-7df915e08cb3`, scenario
`regular-v4-live-activation-off-on-off-256mb`, at HEAD
`dad138284a470342245dc2249c19ca32076c6f12` plus the accumulated working tree.
The affected runtime had not changed after its portable build when inspection
began. No runtime changes or live runs are part of this fix.

The retained input is under
`artifacts/filetransfer-route-acceptance/frontier-dispatch-live-20260913/20260913T133752Z/regular-v4-live-activation-off-on-off-256mb/`.
Its 67,296-line `filetransfer-retained-log-slice.log` has SHA-256
`a7bb3ad037fa6711ea8b091c070884f4ef1db30acbdbf4055116e3a7db77e7ec`.
Fresh evaluation of the complete Coordinator snapshot passes, is stream-complete,
and retains boundary hash
`9988e7372579b3a512d2fb3cdf767aa59a7816ba78a66579824ac172e36d36a0`.
Only after that validation does the isolated probe select 7,859 grace events.

Before the fix, both indexed and flat recent-progress consumers reproduce
Raw/Distinct/Recovered/Unresolved `125/22/21/1`. Stall accounting version 2 is
`32/32/32/0/0` (Raw/Distinct/Recovered/Superseded/Unresolved).
The remaining recent candidate is retained line 20103, receiver writer
`p26960rf24bbb90e33c44f5`, writer sequence 11141, frontier 1926, fallback epoch 2.

| Writer sequence | UTC time | Evidence |
| ---: | --- | --- |
| 11138 | Before recent deadline | Wake 127 rearmed at highest 3973, preserving `13:39:58.6343534Z`. |
| 11155 | 13:39:58.6596254 | Stall wake 16 armed; start `13:39:58.6590253Z`, deadline `13:39:59.4090253Z`. |
| 11157 | 13:39:58.6607734 | Current recent wake 127 returns `rechecked/stall_grace_armed`. |
| 11331 | 13:39:59.0834230 | Three contiguous chunks: 1926 -> 1929. |
| 11338 | 13:39:59.0876771 | Three contiguous chunks: 1929 -> 1932. |
| 11345 | 13:39:59.1007888 | Three contiguous chunks: 1932 -> 1935. |
| 11352 | 13:39:59.1068626 | Three contiguous chunks: 1935 -> 1938. |
| 11359 | 13:39:59.1221338 | Three contiguous chunks: 1938 -> 1941. |
| 11402 | 13:39:59.2072025 | Receiver state 56 publishes frontier 1941, highest 3973, unpaused. |

All five writes and the matching publication precede the existing stall deadline.
No receiver state publishes the intermediate frontier 1929. Later publication of
1980 at `13:39:59.4325034Z` is outside the deadline and is not used as closure.

## Root cause

The shared natural-progress closure admitted only one write whose contiguous
count advanced directly from the episode frontier, followed by publication of
that exact frontier. It could not join several contiguous writes to one coalesced
receiver publication. The partition slice likewise retained only the first
advancing write and its exact-frontier publication, losing the intermediate links
and the coalesced publication when their frontier keys belonged to other partitions.

After fixing that join, replay through the actual full-log GUI consumer exposed
a related input-role mismatch. It rejected the same interval while compact GUI
and retained consumers passed. `filetransfer_data_path_receive_result` carries
the frame's `transport_epoch=0`, not the fallback owner's epoch 1. The emitter
in `NknSignalingTransport.FileTransferTransportChannel.cs` obtains that field from
`IFileTransferTransportMetadataFrame.TransportEpoch` in
`FormatFileTransferDataPathFrameIdentitySuffix`; its authority suffix is separate.
For example, writer sequence 11328 is a receive diagnostic for chunks 1926–1928,
not an owner replacement. Treating every field on every full-log diagnostic as
owner scope created the false conflict. The existing retained catalog excludes
this diagnostic.

## Bounded implementation

1. Accumulate an exact contiguous write chain from the admitted episode frontier.
   Every link must account for its entire advance, maintain highest-received and
   clock monotonicity, and remain strictly before the unchanged 750 ms deadline.
   Zero-contiguous writes may retain the frontier but cannot invent an advance.
2. Require one matching, later, unpaused receiver publication with an increasing
   state epoch and the exact final frontier/highest pair. Missing links, malformed
   counts, contradictory duplicates, source/data changes and late evidence remain
   failures. No runtime action or authority is granted by this read-only closure.
3. Retain the complete bounded catalog interval through eligible publication,
   including intermediate writes, poison evidence and writer-identity copies.
   Candidate ownership and partition width remain unchanged; support records do
   not become independently owned obligations.
4. Share the unchanged grace event catalog from the common handoff script. Apply
   owner-field comparisons to those contract records in both GUI and retained
   paths. Transport diagnostics cannot replace owner scope. Explicit terminal,
   data-session replacement, pause and duplicate-conflict checks remain intact.

No runtime, Coordinator policy, callback effect, route invariant, grace duration,
throughput, retry cadence, bridge, wallet, installer or GUI setup changes.

## Verification and boundary

The isolated evidence ledger is `artifacts/frontier-1926-rca-20260913/`.
The initial chain regression fails on PowerShell 5.1 and 7 before the fix and
passes after it. The frame/owner epoch regression independently fails on both
hosts before the catalog-role correction. Tests exercise flat/indexed consumers,
actual GUI full/compact inputs, independently serialized retained boundaries,
and narrow versus multi-frontier slices. Genuine owner-epoch mismatch and route
replacement are negative controls, not ignored diagnostics.

The adjacent 16-case contract lane passes. Pinned offline reanalysis after the
chain fix reports recent `125/22/22/0` in both flat and indexed consumers, with
the first natural closure at retained line 20375, writer sequence 11402, state 56.
Stall accounting remains `32/32/32/0/0`. The ledger records final consumer replay
and focused follow-up results separately from intermediate failures.

Final verification passes all 22 chain scenarios on both PowerShell 5.1 and 7,
plus the existing single-write suite. Actual-capture full/compact GUI and retained
replays pass on both hosts; normalized retained candidate boundaries match across
hosts. The shared catalog retains exactly the original 27 event names and order.
`git diff --check` passes.

The original live verdict remains FAIL, including its independent `source_dirty`
gate. Offline reanalysis is not a new live acceptance result and does not waive
source provenance. Do not rewrite the old run, receipt or live-results report.
The next live gate requires reviewed clean source, a fresh portable build,
successful escalated preflight, and only the targeted live 256 MiB scenario.
Both peers must show a second Coordinator-published `file_tuna_v4` route as well
as completion and matching SHA. Stop before starting that live run.
