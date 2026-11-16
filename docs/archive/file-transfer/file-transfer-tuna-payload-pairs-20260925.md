# Independent Tuna payload gates: bounded test-only implementation

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus existing uncommitted work.
Implements the approved milestone from
`file-transfer-scenario-gate-handoff-review-20260925.md`.

## Implemented result

The genuine two-process offline fixture now supports a successful authenticated
payload exchange during each Tuna activation. The first pair covers chunks
16-17 and the second 20-21, each carrying 2,048 bytes. Neither range replaces the
delayed Regular packet at chunks 12-13. Its strict result remains
`missing_tuna_resend`, `Recovered=false`; the original classifier is unchanged.

An opt-in `NLINK_PUBLICATION_TUNA_PAIR_SHADOW=1` extends only the test observer's
separate alternative shadow. It checks each pair against the original owned
full and compact Coordinator views, applied forwarding receipt, local route
tuple and publication interval. It matches the actual authenticated envelope,
security establishment/sequence, session/transfer and exact range/bytes/count.
Independent peers' generations are not compared as shared identities.

Both local publications must precede their uses, and a superseding or terminal
boundary before use prevents a pass. A queued send, undispatched receive,
different activation, missing receipt or full/compact disagreement is not proof.
The fixture receives and verifies the original encoded payload through the real
adapter and its data-session receive API; this does not claim a Service disk
write or whole-transfer completion.

The first-activation checkpoint is optional; existing fixtures retain their
default behavior. Test acceleration is cleared after the first pair for the
existing Regular-lane yield exchange, then enabled again for second Tuna.
Coordinator-issued publications remain the authority for both route histories.

## Gates remain independent

With both payloads delivered, `first_tuna_payload` and `second_tuna_payload` pass.
With the second receive deliberately omitted, only the first passes; the second
reports `tuna_pair_receive_missing`. The overall shadow remains **unproven** in
both cases. Scope, complete route cycle, whole-file integrity and remaining
failures are not inferred from these payload results.

The new legitimate second-Tuna traffic exposes a preceding diagnostic limit:
the recorded-rejection consumer reports `dispatch_unbound_generic_observation`
for `filetransfer_data_frame_dispatched`. Its current exclusion helper checks
known frame types and explicit other scopes, but not the generic record's
`chunk_index=20-21`, which differs from the selected rejected range 12-13.
The generic record lacks an authenticated envelope/publication reference.

This implementation does not change that helper, assign the generic record to
a nearby receive, or waive the uncertainty. The positive two-pair case therefore
keeps recorded-rejection admission and candidate coverage unproven. Independent
payload gates are evaluated from their own original evidence even when that
admission is unavailable. The omitted-second-receive case still admits the
recorded rejection; it has no such generic second-Tuna dispatch observation.

The next bounded diagnostic question is whether a strictly parsed, provably
disjoint recorded chunk range can exclude this generic observation. Review that
rule and controls for overlap, malformed/missing range, overflow, wrong frame
type and ambiguous scope before changing the existing predicate. Neither
adjacency nor a different transport label is sufficient attribution. No new
runtime capture or broader acceptance change is authorized by this milestone.

## Custody and limits

The query runs before original primary custody is released. It reuses the
existing parsed original rows and authority indexes, with no source reread,
reader reopen, new authority evaluation or primary witness selection. Native
positions and hashes in the separate payload result are diagnostics, not an
expanded publication-witness export or a new acceptance capability.

Original custody is checked before and after. Full/compact views, original
mismatch proof and preceding diagnostic results are checked unchanged. The
4,096-record, 256-boundary/view, 64-operation/boundary, 64-witness and cumulative
20-second limits remain. Exactly three original evaluations are retained.

Seventeen detached negative controls cover missing receive, wrong range,
payload/envelope identity or byte count, queued send, undispatched/unconfirmed
receive, swapped activation, supersession, terminal boundary, missing forwarding
receipt, duplicate receive, incomplete view, exhausted budget, first-pair reuse
for second activation, and parity disagreement. These mutations are controls,
not newly authenticated histories; the positive evidence is actual fixture
traffic.

## Verification

Final targeted regression: **6 passed, 0 failed**, with every case exercising
PowerShell 5.1 and 7. Receipt:
`artifacts/tuna-pairs-implementation-20260925/results/payload-pairs-final-regressions.trx`.

- Two independent-payload integration cases: both delivered; second receive
  omitted. The positive runs also pass all 17 detached controls on each host.
- Three unchanged strict cases: exact pair recovers; omitted receive reports
  `missing_exact_tuna_receive`; wrong range reports `missing_tuna_resend`.
- Existing security-lifecycle integration preserves default-off behavior.

Final positive capture:
`artifacts/publication-tuna-pairs-20260925/b84f5d9202814fa7ba92a129b70323d5/`.
Final omission capture:
`artifacts/publication-tuna-pairs-20260925/539e529dd3b743e5991071eb555d3070/`.
They retain 367 and 361 full records respectively, 71 passing Coordinator
boundaries per view, complete streams and original full/compact parity. Original
witness counts remain 20 and 24; native pair positions are not added to them.
Cumulative optional work across these four host executions is 2,547-6,889 ms,
within the unchanged 20,000 ms bound. Inputs and preceding results are unchanged.
The default-off capture is
`artifacts/publication-security-lifecycle-consumer-20260925/1c56e6469f5347b18859825bde5a1574/`.

PowerShell parsing and `git diff --check` pass. Source/artifact hashes and
preservation evidence are recorded in
`artifacts/tuna-pairs-implementation-20260925/FINAL-VERIFICATION.json`.

Development receipts are retained rather than overwritten. The first build
attempt hit Avalonia's out-of-workspace telemetry log permission; the build
completed with only that optional build task disabled via
`-p:UsedAvaloniaProducts=`. The initial two tests exposed the fixture's missing
acceleration reset during yield exchange. The next run passed the receive
omission case and exposed the pre-existing generic-dispatch proof gap in the
positive case. The following two-case run passed after decoupling the independent
payload gates from rejection admission. Final verification also includes the
added receive-success and terminal-boundary controls.

Only five pre-existing test/support files changed; the other 76 baseline dirty
files are preserved. Added files are the pair matcher, its controls, the
integration script and this document. Production runtime, strict classifiers,
acceptance, existing receipt profile and historical verdicts are unchanged.
No portable rebuild, live GUI/NKN run, full matrix, commit or push is part of
this milestone. A small offline fixture is not a canonical live-256 positive.
