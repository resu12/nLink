# Service-owned fallback-yield history integration

2026-09-24. Base HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, branch
`v0.7.0`. This completes the two-writer integration gate from
`file-transfer-yield-receipt-binding-review-20260924.md`; it does not enable a
peer-lineage recovery consumer or change acceptance.

## Implementation

The opt-in `PublicationServiceYieldHistory` fixture retains two independent test
hosts and their original loggers, connected only by the existing offline packet
pipe. Both measured yields now use the production Service control-plane sender
and NKN adapter over fake clients. A test bridge forwards each original request
and returns its original result, including the transport-owned diagnostic handle.
It does not manufacture a delivery result or another wire send.

The outbound pre-yield Coordinator history is generated from the Service's
original Coordinator identity and data-session identity. Generation stops before
local yield/proof issuance. At handoff, the same emitter is retained and the
original state is copied into the Service context without another adoption or
authority effect. A calculated state-fingerprint equality assertion protects this
handoff. The complete Service local-yield path then owns quiescence, delivery,
local receipt application and recovery-owner retirement.

Both original authenticated control/bulk receive wrappers reach the Service's
existing receipt entry point. The first peer proof is accepted; its duplicate is
idempotent. The source's later Coordinator route-cycle driver continues from the
actual Service state without applying that peer receipt again. The receiver still
uses the existing Service-owned second-Tuna transition fixture. Thus the measured
yield operations are Service-owned, but this is not a claim that every source
route-transition/setup action is an end-to-end Service workflow.

No measured local receipt is manually created/applied, and no local-proof-sent
effect is emitted by the new test driver. The old pre-authored fixture remains
available with its original default behavior; it is not used as the new positive
capture gate.

## Verified evidence

Final strengthened integration artifact:
`artifacts/publication-service-yield-history-20260924/af93ac7c57804caeae51972ae50400f4/`.

- Two original writers: 153 sender and 230 receiver records; all 383 records are
  retained verbatim in their original per-writer order in the merged source.
- Four distinct encrypted send slots and four matching authenticated dispatches.
- Two Service-owned local receipts and two accepted peer receipts, plus two
  idempotent peer receipts bound only to their own attempted-decision boundaries.
- All 14 new diagnostic records carry `authority_effect=none`.
- Both PowerShell hosts pass complete full/compact authority histories: 71
  boundaries each, zero failed boundaries, and parity `pass`.
- Each original primary still runs exactly three evaluations and reports
  `missing_exact_tuna_receive`, unrecovered, with its original diagnostic variant,
  explanation, witness and completion. Normalized host results agree.

The post-run capture assertions inspect original merged source records and the
already-produced authority reports. They do not run another authority evaluator
or modify primary inputs. The new diagnostics remain outside the unchanged
primary retained projection: 325 full and 162 compact records in this run.
This distinction matters for the next consumer contract: a later consumer cannot
claim these diagnostics were already present in its custody-owned retained views.

## Development findings and scope

The first integration failed strict history validation because the new fixture
called only the lower-level issue/send helpers and omitted the Service's normal
recovery-owner retirement path. It was corrected by invoking the complete
existing Service path, not by manufacturing the missing effect. A subsequent
fixture assertion incorrectly expected checkpoint 100/99; the original source
history actually ends at 8/8. The fixture now uses that original checkpoint.
The initial capture assertion also read the retained slice, which intentionally
omits the new diagnostics; it now reads the already-verified complete original
source. Development failures are retained beside the final test evidence.

This step changes tests/support and this document only. The production capture
implementation, analyzer, authority catalog, security/replay rules, wire format,
runtime scheduling and acceptance remain unchanged. The previous capture seal
has 32 of 33 pins unchanged; the sole extended file is the consumer test class
containing the new opt-in test. The old seal is preserved, not rewritten.

The strengthened integration passed on both hosts, and all 13 directly affected
default-path regression tests passed (14 distinct tests total). Results and hashes
are recorded in `artifacts/publication-service-yield-history-20260924/FINAL-VERIFICATION.json`.
No portable rebuild, live GUI/NKN run, historical reanalysis, commit or push was
performed. The wrapper's GUI-labelled output in these tests comes from its
offline generated-input fixture, not a live GUI scenario.

## Next boundary

The capture-only implementation contract is complete. Next, review the bounded
peer-lineage consumer contract, including
custody of the original diagnostic records and fail-closed association checks.
Keep `peerLineageStatus=not_evaluated`; classifier enablement and acceptance changes
remain separate decisions. A new live-256 run is not needed at this boundary.
