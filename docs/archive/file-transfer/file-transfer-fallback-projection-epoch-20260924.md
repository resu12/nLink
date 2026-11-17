# First fallback projection epoch: RCA and bounded fix plan

## Status and evidence

RCA only; runtime, harness, analyzer, and repository tests are unchanged. Baseline is
`75b2acf30e3b1c687c5ff5ceddd9cca4f6304313` on `v0.7.0`. The latest relevant campaign is
`artifacts/initial-fallback-agreement-live-20260924-75b2acf3/live/20260924T072546Z`.
Its 142 sealed pins and the preceding implementation's 49 pins were verified unchanged.
No new live run, portable rebuild, historical authority evaluation, commit, or push
was performed during this investigation.

The parent retained analysis completed successfully, with complete/parity-passing
snapshots and route consistency PASS. It reports no unresolved coordinator boundary.
The scenario still fails because its second transfer published fallback-v6 rather
than Tuna-v4. That requirement is unchanged. Parent route consistency PASS validates
the routes that were published; it does not prove the required route was selected.

## Root cause and first missing effect

The initial committed fallback does not bind the transport epoch into the session
file-fallback projection that the later reactivation retirement requires.

`NknSignalingTransport.FallbackProjectionRetirement.cs` records
`FileProjectionPublication` for a committed fallback. It sets `FileV6TransportEpoch`
from `FileTransferFallbackTransitionProofBoundary.TargetTransportEpoch` only when
reopening an already retired projection. For the first fallback, the field remains
its default zero unless an independent legacy epoch observation populates it.

The later `CaptureFileFallbackProjectionUnsafe` requires the projection's epoch
to equal the reactivation authority's source transport epoch. Thus a valid committed
reactivation can be applied while its captured fallback projection is null; the old
file-fallback projection is not retired. `IsFileTransferUsingRegularNknFallbackForCurrentSession`
continues reporting fallback. `ResolveInitialOutboundRouteSelection` preserves that
fallback input and does not request initial Tuna admission while it is true.

The existing retirement fixture hid the dependency: after applying fallback commit,
it explicitly called `ObserveFileTransferV6TransportEpoch(... Recovered ...)` before
reactivation. The real committed cleanup marks the file lane recovered but does not
write its epoch. The fix belongs at the already-authorized fallback disposition,
not in route-resolver precedence or a terminal cleanup that clears session state.

## Current live sequence

W below is the original scenario's `filetransfer-gui-log-writer-evidence-slice.log`.
The bounded source lines are retained verbatim in the RCA evidence artifact.

| UTC | Observation | Source |
| --- | --- | --- |
| 07:27:16 | Intended local/peer switch-off starts fallback epoch 1 | W:18974, W:19120 |
| 07:27:17 | Both fallback dispositions are committed; cleanup reports proof transport epoch 1 | W:19296, W:19299, W:19340, W:19344 |
| 07:27:19 | Intended payer unlock completes | W:21207 |
| 07:27:37–38 | Both reactivation pause bindings and committed dispositions are applied, with source transport epoch 1 | W:30194, W:30503, W:30540, W:30746 |
| 07:27:38 | Both first-transfer Tuna-v4 successor publications are accepted | W:30500, W:30734 |
| 07:27:52 | First transfer completes on Tuna-v4 in both directions | W:45138, W:45187 |
| 07:29:00 | Second transfer publishes fallback-v6 in both directions | Sender sequence 31630; receiver sequence 15045 |
| 07:29:01 | Original fallback epoch 1 remains recovered, with `file_v6_epoch_state=unknown` | W:46831 |

There is no `filetransfer_fallback_projection_retired` event in the retained writer
slice. There is only the intended payer `tuna_runtime_locked` event before reactivation;
the previously fixed redundant second-transfer toggle did not recur.

The live artifact does not serialize every private capture predicate. Accordingly,
absence of the retirement event alone is not treated as proof of a unique internal
branch. The current-source reproduction below establishes the epoch defect and its
ability to produce the observed stale fallback selection. Other future admission or
readiness failures are not ruled out; successful second-transfer Tuna is not promised
by this RCA.

## Deterministic reproduction

Artifact directory: `artifacts/fallback-projection-epoch-rca-20260924`.
`ProjectionEpochProbe.cs` is injected only into an isolated test build using
`Probe.targets`; no repository project or source file is edited. All build outputs
are redirected under this artifact directory so previously sealed binaries remain
unchanged. The fixture uses the existing transition test helpers, real transport
pause/disposition methods, a fake client, and disabled Tuna; no live network or GUI.

Four characterization cases pass: inbound/outbound, each without and with the legacy
epoch-observation control. They establish that:

1. The committed fallback carries target epoch 7 and binds its publication, but the
   projection retains epoch 0 and no epoch-state value.
2. The fixture models only the recovered diagnostic lane state produced by the live
   committed cleanup. It does not install an execution shadow or grant authority.
3. Without the additional observation, a subsequent valid reactivation disposition
   is Applied, yet fallback remains active and no retirement tombstone exists.
4. With the epoch observation, the same authorized sequence retires the projection.
   Neither case grants Tuna readiness merely by retiring fallback.

These are characterization passes confirming the defect, not a green fix regression
or scenario acceptance. `tests/projection-epoch-probe.trx` retains all four results.
An earlier command-line quoting error stopped before build/test execution and is
retained separately as `probe-invocation-error.log`.

## Bounded implementation contract

1. In the exact current, validated committed fallback disposition, bind a previously
   unbound file projection's epoch from the command's fallback proof boundary. Require
   the expected fallback-v6 route/protocol/transport, a positive target epoch, and the
   same publication/session/transfer/direction scope already enforced by the bridge.
   Do not infer the epoch from a listener, route cache, frame observation, or timer.
2. Initialize only an unbound epoch. Preserve a matching observation-populated epoch;
   do not overwrite a conflicting/newer epoch, replaced projection, retained
   predecessor, or newer local/peer stop. Keep the existing exact committed-reopen
   behavior for an already retired projection. Do not clear the whole fallback state.
3. Keep the retirement predicate, coordinator authorization check, publication and
   generation comparisons, stale/idempotent guards, screen state, and pending
   control-plane owners intact. No new authority type or reevaluation is required.
   Binding the epoch is not itself retirement, route publication, or Tuna readiness.
4. Convert the unobserved cases into true red/green regressions: the first commit must
   bind its epoch, and later authorized reactivation must retire without a legacy
   epoch observation. Retain observation-fed controls and both directions. Add exact
   negative controls for missing/wrong proof boundary, invalid target epoch, mismatched
   scope, a conflicting/newer epoch, duplicate/stale commit, and a newer stop.

Expected production scope is `NknSignalingTransport.FallbackProjectionRetirement.cs`
with directly affected retirement tests. If implementation needs a broader authority
or lifecycle change, stop and revise the contract rather than introducing a heuristic.

The GUI's `authoritative_snapshot_pass_harness_failure_unclassified` is a fail-closed
reporting limitation after the missing required publication. Leave classification,
timeouts, route expectations, wallet/UI actions, throughput policy, and readiness
reminting unchanged in this patch. Do not label that reporting category a demonstrated
harness-only runtime cause or treat SHA success as acceptance.

## Verification order and next gate

Run the new red/green retirement regression first. Then run the directly affected
projection-retirement and pause/disposition guard tests once, plus the existing
second-transfer admission controls that cover this path. Do not repeat unrelated
analyzer/contract aggregates. Preserve all prior artifact pins.

After review/checkpoint and a fresh portable build, rerun only
`second-transfer-after-reactivation` outside the sandbox with both strict preflights
and no implicit retries. Require retirement of the exact old projection, fresh
coordinator admission/publication of second-transfer `file_tuna_v4`/4, and the normal
paired completion/integrity/evidence checks. Advance to a newly observed first proof
edge if this exposes one. The full GUI matrix remains gated on this targeted result.
