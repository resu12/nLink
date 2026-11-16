# Initial Tuna local-stop callback capture: RCA and bounded fix

Base revision: `c426b2efacd3b36863e127552eb9d148fa5351fe`.

## Evidence and first missing edge

The current-revision targeted live attempt is
`artifacts/initial-tuna-fallback-checkpoint-20260921-c426b2ef/live/20260921T170507Z/live-switch-off-helpee-64mb`.
All three live preflights passed. Both endpoints published ordinary initial
Tuna V4 (publication 1, epoch 0, absent commit), but switch-off never established
the fallback route. No completed-transfer or performance success is claimed.

The retained log slice has SHA-256
`d2d5f9919f44a5375107bf698234c1d1648b620993dbaf493015c3f85aae3ac7`.
Line 6940 accepts the outbound source-hold preparation; line 6941 invalidates it
with `bootstrap_source_hold_outbound_callback_unbound`. Line 7004 rejects the
inbound initiation. The first missing fallback edge is the outbound capture,
not a missing transport retry or a permissive route-publication rule.

Decoding the 2,086 source snapshots through line 6941 with the correct base64url
alphabet leaves six admitted, unconsumed callbacks, leases 1031 through 1036
(admitted at lines 6882 through 6887). All carry provider generation 1,
source-pause instance 0, and the same exact initial source and agreement as the
hold. The live operational log records the legacy
`filetransfer_v4_sender_pump_paused_for_file_tuna_v4_fallback_handoff` event
before the hold preparation. The stop handler had already retired the admitted
lane and reset negotiation before delivering the copied local-stop source.

This explains the pause mismatch: callbacks admitted unpaused become stale when
the legacy failed-send handoff mutates Service pause state. The strict capture
check correctly refuses to bind them to that different pause. The combined
live rejection does not name an individual failing subpredicate; the causal
interleaving is supported by these exact records and reproduced offline, not
claimed as a new authoritative evaluation of the entire live artifact.

Correction to the earlier diagnostic note: the exploratory decode failures
were caused by treating base64url as ordinary base64, not by established
snapshot corruption. No secondary acceptance analyzer was run.

## Reproduction and fix

The new joined regression uses real prepared admission, Service/adapter session
binding, Core callback issuance, and six registered physical sends. It models a
failed completion at the reset notification using the actual currentness check
and legacy pause helper. On unchanged production code, both payer-sends and
dialer-sends cases invalidate the hold, reproducing the first live edge.

For an exact correlated local stop, the adapter now reserves the existing
fallback observation/epoch identity and synchronously delivers the copied
source before negotiation reset changes send eligibility. Core alone validates
the source and installs the hold. Only then can reset and admitted-lane
retirement expose failed callbacks. Reset reuses the observation identity;
it must not issue a second generation or restart the fallback epoch after the
hold has captured its source.

Stops without a captured correlated source retain their original early
preparation-retirement order. If negotiation was already reset, the existing
post-reset exact-source delivery is preserved instead of dropping that local
stop. A separate joined pair covers this already-reset case.

Moving physical retirement alone is insufficient: reset itself causes
`TrySendAcceleratedEnvelopeAsync` to reject previously eligible sends. The new
test checks that the local-stop delivery occurs while the admission and
negotiated source are still current, and that all six callbacks resolve Held
after stop. No hold receipt or bootstrap exchange may exist until the physical
tasks actually settle. After modeled physical failure, the existing joined
fixture requires both fallback publications and a completed four-byte payload
with matching SHA. It is not a 64/256 MiB load or goodput test.

The strict callback/source checks, Core hold/capture/receipt contracts, stale
callback dispositions, route invariants and analyzer schemas are unchanged.
No queue, timer, retry, chunk-size, wallet, installer, GUI or screen-share policy
is tuned. Scope is local stop, not independent transport failures.

## Verification and remaining gate

Artifacts: `artifacts/initial-tuna-stop-ordering-20260921`.

- `before-regression.trx`: completed, 2/2 expected failures on original production
  code, both at the invalidated source-hold assertion. An earlier fixture build
  error and event-label mismatch were corrected before this reproduction.
- `after.trx`: completed, 2/2 passing focused cases.
- `affected.trx`: completed, 76/76 passing. Includes the new pair, all initial
  fallback directions/stop modes, owned-write currentness controls, initial
  feedback/evidence controls, preparation retirement, eight Core callback
  currentness cases, 28 source-failure effect-boundary cases, and three physical
  hold-settlement cases. These overlap the focused pair; do not add totals.
- `controls.trx`: not a completed passing lane. Six committed-Tuna joined
  fallback cases passed, two legacy controls failed, and the 45-second hang
  collector aborted during `TransportAccelerationStop_RemoteDownDoesNotBlockPeerPaidReunlock`.
- `baseline-controls.trx`: with both production files restored exactly to HEAD,
  the same two controls failed at lines 2808 and 28292 respectively, and the
  same third test exceeded the 45-second inactivity bound. The bounded fix was
  restored afterward. This establishes baseline reproduction of those three
  issues, not their root cause or resolution.
- `final-focused.trx`: completed, 8/8 passing after the final preservation edits:
  the callback-capture pair, already-reset pair, and four preparation controls.
  This overlaps `affected.trx` and is not an additional independent total.
- A separate `final.trx` compatibility check was aborted while
  `TransportAccelerationStop_RuntimeUnlockClearsUserStoppedSessionGuard` was
  active. That fourth control has not been separately baselined; its cause is
  unclassified. Seven preceding cases passed, but the run is not green. Full
  details, including the selected control with no result, are in the artifact
  results file. Compatibility issues are not claimed fixed.

The prior live classifier timeout and missing parent-owned finalized analysis
remain separate reporting issues. No new commit, portable build, GUI run,
live-256 run, full matrix or push belongs to this offline step. Review the
bounded diff and verification limitations before checkpoint/rebuild and another
strictly preflighted run of the same targeted 64 MiB scenario.

### Follow-up compatibility review

`file-transfer-stop-ordering-compatibility-review-20260921.md` records the next
review. The fourth failure is now baseline-confirmed; the two former collector
aborts finish at their own timeout assertions when the harness allows that.
The blocked-notification control passes on both versions, and the restored
fix completes a five-case focused run. No new blocking defect was found in the
bounded production diff. The authority-correct repair plan for the four legacy
fixtures remains a separate, explicit gate; the compatibility suite is not
claimed green. No additional runtime/test change, commit or live run occurred.

### Subsequent fixture repair

The compatibility review's implemented-repair section and
`artifacts/initial-tuna-stop-ordering-20260921/fixture-repair/RESULTS.md` now close
that scoped offline fixture gate: 31 affected cases and six final controls pass
in completed runs. Unbound requests/generic completions remain negatives;
positive preparation/admission and exact committed proof retirement have real
Coordinator-backed coverage. The original production diff is unchanged. This
does not establish full compatibility or live-load acceptance; checkpoint,
fresh portable and the targeted preflighted 64 MiB rerun remain next.
