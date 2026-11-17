# Independent retry confirmation: terminal eviction and remaining install handoff

Baseline: `4d96cfb1a62481eeb21c6220a5214fd773d52d48`, branch `v0.7.0`.
Offline evidence: `artifacts/retry-confirmation-terminal-contract-20260923/`.
Latest live evidence remains `artifacts/post-fallback-eof-live-20260923-4d96cfb1/`.
No live campaign was re-analyzed or promoted by this work.

## Proven producer/catalog mismatch

The primary live failure is the accepted confirmation compensation at writer
`p42836re1e39827497f45dd`, sequence 23601, transition
`fte1-ff1d26394420b49dad80dc79`, coordinator generations 52 -> 53.
The new independent retry is removed, its previously settled DispatchObserved
terminal is evicted, and the new retry becomes Invalidated. No predecessor is
restored. The old catalog admitted only two or four operations, not these three.

A real coordinator fixture now reproduces both retry installations, the first
DispatchObserved settlement, exact paired-yield supersession and successor peer
proof, and the second install's rejected confirmation. The coordinator test
passed while the original analyzer rejected the three-operation shape.

The bounded catalog change requires:

- Exactly the failed installed attempt, its invalidated terminal, and one older
  DispatchObserved terminal eviction; no restored predecessor or active
  predecessor supersession.
- The exact immediately preceding accepted install commit, including generation,
  hash, boundary, operation identity, and typed receipt payload.
- The old terminal's exact accepted settlement origin in the same writer,
  coordinator, session, transfer, and direction.
- No terminal-slot mutation at or after that origin through the new commit,
  including foreign terminal overwrite-and-restore attempts.
- No physical authority effect attributed to compensation.

Two- and four-operation contracts remain unchanged. The frozen catalog hash and
decision list are unchanged. This is analyzer admission of a proven compensation,
not permission to dispatch or a claim of successful route recovery.

The combined fixture retains real Family-2 and paired-yield lineage. Its declared
initial state has no pending checkpoint; it does not erase a checkpoint during
the tested stream. Physical effects are explicit receipt-backed unit fixtures.
They are not evidence that a live transfer or complete route cycle succeeded.

## Verification

The affected compensation lane passed **32 tests, zero failures**, including
existing two/four-operation, active-predecessor, stale-state, and terminal-rollover
cases. The new evidence test runs on Windows PowerShell 5 and PowerShell 7 with
identical normalized results: eight history controls, eight shape/identity
controls, and six full/compact cases. Exact, reordered, and duplicate evidence
remain valid; missing terminal history and forged compensation effects fail.
Missing new-install effects retain their exact semantic failure on the original
install commit (artifact structural validity alone is not semantic acceptance).

The initial red run, fixture setup corrections, and all follow-up TRXs are retained.
`test-audit.json` reports the latest result per selected test rather than claiming
that intermediate runs passed. `FINAL-VERIFICATION.json` pins this milestone and
checks the prior live manifest read-only: 157 unchanged files and one intentional
analyzer-source delta. No older primary receipt or pinned artifact was rewritten.

## Separate runtime rejection: evidence and limits

The operational log at
`live/20260923T123740Z/second-transfer-after-reactivation/live-operational-log/nlink.log`,
line 38929, records sequence 23600, install/authority generation 2,
`predecessor_authority_id=(none)`, and
`retry_dispatch_install_confirmation_receipt_mismatch`.
This precedes the compensation snapshot, rather than being caused by the analyzer.

Source inspection identifies a likely missing retained-install retirement:

1. DispatchObserved moves the coordinator attempt into its terminal slot.
2. The post-fallback EOF path rolls back the route transition and supersedes the
   paired yield, then resolves the next yield. That path does not explicitly
   retire the transport's last committed install receipt.
3. Confirmation treats any different retained committed receipt as a predecessor
   and requires exact predecessor identity on the new authority and contracts.
   An independent authority has no predecessor and cannot satisfy that branch.

This is a source-supported cause to reproduce with both real services/transports;
the logged reason is shared by more than one receipt mismatch branch. This
milestone does not claim that adapter field contents at that instant have been
proven, nor that this fully explains the later sender `peer_disconnected` failure
or the original physical Tuna EOF.

The coordinator already supports exact FullScope invalidation of a retained
DispatchObserved terminal via `GetRuntimeUnlockRetryLifecycleAttemptForScopeInvalidation`.
The adapter checks the install receipt, authority, request, offer alias, settlement,
and generation before applying that command. However, its allowed reasons do not
currently include failed-probe retry-cycle retirement. Re-labeling a same-scope
rollback as data-session or runtime-scope replacement is not an acceptable fix.

## Next bounded runtime work

1. Extend the joined EOF regression beyond pair supersession to the next paired
   retry. Capture which confirmation comparison rejects the exact new receipt.
2. Reuse the coordinator-owned invalidation command only if the joined test proves
   it retires the exact settled old install without invalidating the fresh paired
   yield. Add a narrowly validated probe-rollback reason, not a generic reset.
3. Order accepted rollback, exact install retirement, and successor retry release.
   Failed staging/apply/commit must not release the successor. Repeated or stale
   callbacks must not retire a replacement install or emit route/runtime effects.
4. Test both EOF reasons, exact receipt retirement, rejected/forged receipts,
   late callbacks, successor preservation, and unchanged fallback publication.
   Then run only directly affected coordinator/service/adapter/analyzer lanes.
5. Only after the joined continuation passes: review/checkpoint, rebuild portable,
   strict escalated preflights, and one targeted `second-transfer-after-reactivation`
   attempt. Do not run the full GUI matrix while this correctness edge is open.

No receipt check, runtime policy, throughput threshold, queue, chunk size, or retry
cadence has been changed in this milestone. The previous live verdict remains FAIL.
