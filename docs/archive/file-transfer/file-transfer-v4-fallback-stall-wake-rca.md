# V4-compatible fallback writes leave the stall wake behind

2026-09-15. Investigated against clean baseline
`adc5a980f9a02cc44c14e90d35b90f744bc24e3d` and its single current live run
`artifacts/publication-terminal-eof-live-20260915/runs/20260915T113358Z/regular-v4-live-activation-off-on-off-256mb`.

## Evidence and first missing edge

The original primary report remains FAIL/incomplete_proof. Delivery, SHA and both
peers' full route cycle passed; they do not close the reported stall obligation.
No historical primary evaluator was rerun and no captured artifact was changed.

In the retained log, receiver writer `p53752r798cddfd10224035` records:

- Line 22308 / sequence 10151: stall wake 8, frontier 4395, highest 5877,
  due at 11:36:16.8789804 UTC. Source is fallback-v6 epoch 2, runtime/leg/
  publication generation 3, checkpoint/data-session generation 1.
- During that grace interval the original writer ledger records accepted
  `filetransfer.chunk_batch.v4` traffic on the same fallback-v6 publication
  (for example sequence 10207, chunks 5887 through 5889).
- Line 22608 / sequence 10326: receiver state still has frontier 4395, with
  highest now 5985. There is no intervening stall-wake refresh.
- Line 22633 / sequence 10343: the exact captured wake runs and is rejected as
  `stale / frontier_scope_changed`. This is not a missing timer callback.
- Line 22664 / sequence 10353: a later chunk-commit path independently obtains
  repair generation 13 at frontier 4395/highest 6006. Its continuity origin
  differs from the hold's source, but `continuity_current_scope_fingerprint`
  equals the hold's `fte1-c50440aace9013475700d9ab`; the different origin alone
  is not evidence of a bad owner.
- The frontier remains 4395 through subsequent stalled repairs and the epoch-3
  Tuna handoff. Successful-progress closure is therefore not established in
  that fallback scope. The diagnostic/supersession helper requires a dispatched
  timer result and cannot admit this stale result as dispatch authority.

The runtime discrepancy is between the two sparse-write commit paths.
`HandleInboundV6ChunkBatchAsync` refreshes a current stall wake after accepted
tail-only writes. `HandleInboundV4ChunkBatchCoreAsync`, used by v4-compatible
traffic on a regular-v4-origin fallback, advanced the same received-highest
state but omitted that refresh. The old wake retained its previous highest and
correctly failed exact currentness when its deadline arrived.

## Bounded correction

After a current v4 sparse-write commit, call the existing
`RefreshInboundV6FrontierStallGraceWakeForHighestProgressLocked` under the service
gate, then schedule a successfully refreshed wake outside the gate. This is nine
added production lines, including comments and the scheduling block.

The existing helper requires the current fallback-v6 runtime, exact owner,
publication, leg, session, transfer and data-session scope, an unchanged frontier
and an increasing received-highest value. It retains the source stamp, stall
start and original deadline. Only the wake generation and highest are replaced.
Old callbacks remain stale and diagnostic-only. The existing Coordinator path
still decides whether a frontier request may be dispatched.

No analyzer rule, authority contract, route invariant, 750 ms production grace,
retry cadence, queue, chunk size or throughput policy changed. This does not
claim to resolve the two recent-progress or 71 Tuna data-path unresolved cases.

## Deterministic verification

The existing service fixture now covers v4 tail writes through the production v4
receive handler, alongside its v6 controls. The fixture's v6-only receive loop
does not accept v4 frames, so the handler invocation is explicit; this is not an
end-to-end live-network test. Its epoch/publication and subsequent repair are
Coordinator-issued, including value-equivalent Coordinator-state adoption.

- RED before the production change: both v4 cases fail `Assert.NotSame` because
  the accepted write keeps the old wake; four v6 controls pass.
- GREEN: all 8 tests in `PostTunaFallbackV6FrontierStallGrace` pass. Assertions
  cover deadline/source/checkpoint/data-session preservation, incremented wake
  generation, stale old-wake rejection, exact later request and committed
  progress. Changed session/transfer, owner, publication, epoch, leg, checkpoint,
  data session, terminal and pause controls reject refresh.
- Adjacent checks: 7 v4 write-binding cases plus 3 focused stall analyzer tests
  pass (10 total). No aggregate suite or GUI matrix was run.
- `git diff --check` passes. Original retained and writer-ledger SHA-256 values
  remain unchanged. Test history is in
  `artifacts/publication-stall-v4-closure-20260915/RESULTS.md`.

## Next gate

Review/checkpoint this bounded change, rebuild the portable package, then obtain
fresh authorization for one strict-preflight
`regular-v4-live-activation-off-on-off-256mb` run. The previous single-run
authorization is consumed. Do not reuse the old executable, reclassify the old
run, launch the full matrix, or assume the remaining proof failures are fixed.

The absent optional local-publication companion remains a separate diagnostic
issue; its absence does not justify relaxing primary proof or custody rules.

### Authorized targeted validation

The user subsequently approved the local checkpoint, portable rebuild and one
new strict-preflight live-256 run on 2026-09-15. Use an isolated LocalOnly build,
zero setup/goodput reruns and one fallback attempt. Stop on a failed preflight;
no push, full matrix or historical reclassification is included. Record the
original outcome under `artifacts/publication-stall-v4-live-20260915`.
