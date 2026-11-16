# C2e: receiver service transition prerequisite and live source cross-check

2026-09-15. No production change, new live run or historical authority
reevaluation. Earlier uncommitted work is preserved.

## Implemented prerequisite

The C2d two-writer fixture directly supplies coordinator publications to the
transport; it has no actual receiver service context. Attaching a new context to
that precomputed history or calling only a log formatter would not reproduce the
service's prepare/commit/disposition path. C2e therefore adds a focused service
regression before attempting that integration.

The existing receiver fixture supplies controlled runtime-unlock proof and a
recording/loopback bridge. It runs the actual service preparation, physical-drain
recheck, coordinator commit, target-context installation, guarded compatibility
projection and disposition application. The new test captures the original
execution after preparation without changing it. No transition record may exist
at that point. After commit, exactly one inbound regular-v4 to Tuna-v4 epoch-1
record must agree with the committed disposition's publication and current
runtime owner. A second case advances through the real service fallback-bootstrap
path and verifies the Tuna-v4 to fallback-v6 epoch-2 record.

Both cases replay the exact original drain execution twice after completion
(and, in the second case, after route replacement). The coordinator state
fingerprint/persistent state, current publication, projection keys, transition
records, observed route hints and sent-frame count must remain unchanged. No
publication-forwarding or recovered-epoch effect may be emitted by those calls.

This proves a service-level prerequisite only. It does not produce a service-owned
second-Tuna epoch-3 route in the two-writer fixture, execute a live NKN/Tuna bridge,
or establish data-path recovery. The original C2c and C2d negative cases remain
unchanged. Raw transition authority IDs remain redacted; exact identity is checked
on the coordinator objects, not reconstructed from the redacted log field.

## Additional read-only live evidence

C2a's retained input still hashes to its original receipt value:
`6044c04c4bdba0c3084c6c6ae225ccb713b0ce731273165d3379f1483d581d5c`.
Only original source records and source code were read; neither its authority
evaluator nor its recovery classifier was invoked again.

The receiver transition missing from C2d's controlled fixture is present in C2a:
retained line **24610**, receiver writer `p22604rdd2349a0967a4f5e`, sequence
12729. It precedes the selected rejected receive at line **24759**, sequence
12768, and reports fallback-v6 to Tuna-v4, epoch 3, protocol 4, runtime/leg/
publication generation 4, terminal generation 0 and commit generation 2. Its
redacted session/transfer fields agree with the selected receive. Thus the C2d
missing-transition result must not be presented as the live root cause.

For the selected exact range, start 6930 / end-exclusive 6933, raw bytes 64512,
batch count 3, the retained payload records contain:

- Line 13798: original regular-v4 epoch-0 send.
- Line 23018: fallback-v6 epoch-2 send of the same range, not a Tuna-v4 resend.
- Line 24759: the original old regular envelope rejected at Tuna-v4 epoch 3.
- Later fallback-v6 epoch-4 sends/receives, beginning with send line 33397.

The exact-range search across this retained capture found no Tuna-transport
payload send or receive record. The current rejection-recovery rule separately
requires an exact-range Tuna send and receive for the post-fallback second-Tuna
case. Missing resend evidence is therefore a supported next investigation target,
not a claim that we recovered the original primary's unpersisted internal reason.
This source check does not prove every one of C2a's 13 unresolved records has the
same cause, or that the overall failure is a false positive.

## Next bounded step / fix-plan boundary

Review the proof contract for an exactly identified old regular-route envelope
that is rejected before dispatch under a current Tuna publication. In particular,
decide whether the existing exact-Tuna-resend requirement is necessary for this
case, or whether any diagnostic classification change needs a separately proven
source/receiver publication relationship. Do not remove that requirement merely
because the receiver guard behaved correctly or the final file SHA matched.

Before any production change, add a fresh deterministic case that reaches the
existing resend predicate through the actual current service transition path.
Retain missing/wrong publication, wrong-writer/source, accepted-on-wrong-route and
stale-callback negatives. Keep original evaluation count, source ownership and
bounded work unchanged. The local-publication companion still proves no peer
lineage and must not be silently broadened into such authority.

The service-owned epoch-3/two-writer integration remains open. No additional
runtime/correlator fix is justified by C2e alone; stop at a new targeted live
256-MiB gate if that integration requires live inputs.

Focused results and development assertion corrections:
`artifacts/publication-inbound-service-transition-20260915/RESULTS.md`.

## C2f continuation

The standalone receiver service epoch-3 prerequisite is now implemented in
`file-transfer-second-tuna-service-publication-rca.md`. It exposed and fixed an
exact retirement-effect binding loss. The two-writer service/payload integration
is still open; C2e's live-input conclusions and the resend contract are unchanged.
