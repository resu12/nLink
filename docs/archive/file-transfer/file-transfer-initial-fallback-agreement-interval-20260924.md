# Initial fallback agreement interval: RCA and bounded fix plan

## Status and authoritative result

RCA only, on `v0.7.0` at `1bed386d61388d383eff2cd33a54cf7e97bc0953`.
No runtime/analyzer change, test execution, build, GUI rerun, commit, or push is
part of this step. Historical authority analysis is not rerun.

The current live campaign is
`artifacts/pressure-settlement-generation-live-20260924-1bed386d/live/20260924T062257Z`.
Its single `second-transfer-after-reactivation` attempt remains
**FAIL / correctness / incomplete proof**, with performance PASS. The original
parent receipt is valid and completed; snapshot artifact evidence, stream
completeness, and full/compact parity passed. Route consistency failed with
`snapshot same-route publication has no exact fallback owner successor contract`.
Payload SHA matches do not replace the required second-transfer Tuna-v4 publication.

Parent-selected boundary: `fte1-e391525977c8388cb5b88893`, SHA-256
`69e1c392172964b90aa02becbab99180d4e35895ada7dc55a711ddc67de54052`,
writer `p24556r91ae06f7ace945be`, sequence 32365, outbound.
It is an accepted `StartTransfer` / `post_tuna_fallback_v6_route`: fallback
protocol 6, live epoch zero, publication/runtime/leg each 1 to 2, bound data
session unchanged at generation 1. This remains the selected failure; the
earlier witnesses below explain its interval, not a new authority selection.

## Exact witness chain

Source is the scenario's `filetransfer-retained-log-slice.log`, 39,938 lines,
SHA-256 `a4e5f3acf8aae1b10a0bc3dd534809b4e3708ff0f26012bcd7333a56665132eb`.
Seven exact source snapshots are decoded and checked against both their declared
payload hashes and the original parent's normalized boundaries.

| Source line | Writer sequence | Decision | Aggregate generation | Relevant operation |
| --- | --- | --- | --- | --- |
| 33031 | 32290 | PinFallbackBootstrapOffer | 0 -> 1 | Agreement None -> Offered |
| 33032 | 32291 | StartTransfer | 1 -> 2 | Initial runtime owner |
| 33034 | 32294 | RoutePublication | 2 -> 3 | Initial publication 1 |
| 33080 | 32331 | ApplyFallbackBootstrapAccept | 3 -> 4 | Agreement Offered -> Enabled |
| 33098 | 32357 | ApplyFallbackBootstrapSessionOpen | 4 -> 4 | Idempotent; no operations |
| 33100 | 32359 | BindInitialFallbackDataSession | 4 -> 5 | Exact binding 0 -> 1 |
| 33104 | 32365 | StartTransfer | 5 -> 6 | Owner/publication successor |

The generation/hash chain is continuous. Agreement acceptance changes persistent
negotiation state, but preserves the entire route/runtime boundary tuple. The
session-open observation preserves both hash and tuple. The binding then changes
only data-session identity/generation and names the exact initial owner and
publication fingerprints. Startup contains the expected five operations: old
owner/publication retirement, new owner/publication installation, and attempted
command.

## Root cause

`Test-FileTransferCoordinatorInitialFallbackStartupInterval` in
`tools/FileTransferOps/InitialDataSessionEvidence.ps1` walks every interval
boundary, including non-mutating observations. Its non-binding branch (lines
146-164 at this HEAD) requires each operation to match either the preceding
publication key or the preceding runtime-owner key. Otherwise `$prior` is null
and the function returns false.

The first interval operation here is the accepted `FallbackBootstrapAgreement`,
role `fallback_bootstrap_agreement`, at line 33080. Its operation key matches
neither runtime key. It is therefore rejected by this branch before the reducer
reaches the valid binding and selected startup. The caller,
`Test-FileTransferCoordinatorSnapshotFallbackOwnerPublication`, propagates the
false result into the parent's same-route successor finding.

This is an analyzer composition gap: the startup interval's operation whitelist
omits coordinator-owned negotiation that preserves route/runtime ownership.
It is not evidence that startup must be removed or that transport observations
should issue authority. `ApplyFallbackBootstrapAcceptCore` checks the pinned
offer, current coordinator/session/transfer/direction, protocol, and selected
issuer, then updates only `BootstrapAgreement`. Its snapshot advances aggregate
state without granting a route, runtime unlock, or publication. The existing
bootstrap catalog recognizes outbound `Offered -> Enabled` acceptance and
validates agreement history before the parent's artifact-evidence verdict.

The agreement's stored source boundary is its immutable offer-time scope, before
owner/publication creation. It must remain linked to that offer; it must not be
rewritten or equated to the current publication-time boundary.

This proves the first blocking predicate, not that every downstream contract
will pass after it is corrected. No alternate route verdict was computed.

## Why offline coverage missed it

- `InitialFallbackStartupFixture` uses `LoopbackFileTransferTransport` without
  overriding `SupportsFallbackBootstrapCorrelation`; the interface default is
  false. The service fixture consequently lacks the live negotiation interval.
- `CreateInitialFallbackPublicationSnapshotFixture` does pin a correlated offer,
  but its startup branch goes directly from first publication to binding and
  startup. It never calls agreement acceptance/session-open in that interval.
- Existing malformed-binding and successor controls therefore did not exercise
  composition with a valid accepted agreement. Their passing results remain
  valid for their narrower covered paths.

## Bounded implementation plan

1. Add a fresh production-emitted regression before changing acceptance. Use a
   test-local correlated loopback transport for ordinary service send/accept/open,
   covering both directions. Require actual agreement acceptance in the interval;
   do not fabricate positive snapshots or replay the live log. Also extend the
   Core fixture with real coordinator accept/open decisions and independent
   owner, leg, and publication generations. Record the expected current failure.
2. Extend only the initial-fallback interval reducer to admit the exact existing
   agreement lifecycle in addition to its current binding and unchanged-token
   cases. Reuse the bootstrap operation/history contract, with the exact pinned
   offer anchor from the same writer/coordinator/session/transfer/direction.
   Preserve the complete aggregate hash/generation chain, every observation,
   and exact unchanged route/runtime scope. Limit negotiated mutations to the
   catalog's accepted outbound accept and inbound accept/session-open transitions
   before the first binding. Keep the outbound empty idempotent open valid.
   Existing no-agreement fixtures remain valid.
3. Require immutable agreement identity, generation, offer-time source scope,
   issuer continuity, and exact allowed fingerprint/state progression. An
   agreement is negotiation evidence only: never use it as a runtime owner,
   publication, prior committed Tuna transition, bootstrap-delivery proof, or
   replacement for the first-session binding. Do not skip all `route_transition`
   operations, trust a reason string alone, or weaken rejected-callback guards.
4. Preserve every existing startup successor requirement: exactly one binding,
   unique retirements/entrants, immutable retired tokens, independent generation
   increments, exact publication receipt and forwarding effect, no stale authority
   reuse, and unchanged `state_refresh_received` transition/bootstrap contract.

Expected production scope is analyzer-only plus focused tests. If fresh evidence
reveals a missing producer witness or a different required runtime change, stop
and revise the RCA instead of expanding this fix implicitly. No catalog/payload
format change, throughput/queue/chunk/retry tuning, or timeout relaxation is planned.

## Verification and next live gate

The new positive service/Core fixtures must pass full and compact consumers on
Windows PowerShell and PowerShell 7. Negative controls must reject missing or
duplicate offer/accept/open history; wrong writer/coordinator/session/transfer/
direction; swapped authority, issuer, fingerprint, generation or offer scope;
illegal ordering/state/status; hash gaps; route/runtime/data-session changes;
hidden retire/restore; exchange/phase or unrelated operations; missing/duplicate
binding; and stale publication/effect evidence. Rejected callbacks must remain
diagnostic-only. Retain existing no-agreement and checkpoint-refresh controls.

Run the focused regression first, then directly affected initial-fallback,
bootstrap-agreement/history, and successor lanes once after stabilization. Do
not repeat aggregate suites. Review/checkpoint the bounded diff, rebuild the
portable package, then run strict preflights outside the restricted sandbox and
only `second-transfer-after-reactivation`. A fresh parent-owned analysis must
establish the required second-transfer coordinator-published `file_tuna_v4` and
terminal/integrity results. Full GUI matrix stays gated.

The pressure-settlement generation fix remains offline-verified only: this live
campaign contains zero `CommitRegularV4FrontierRepairPressureSettlement` boundaries.

## RCA integrity

`artifacts/initial-fallback-agreement-interval-rca-20260924/Verify-Rca.ps1`
checks the sealed live manifest and all 142 pins, the seven exact source payload
hashes, their parent-boundary identities, and the factual chain above. It writes
`WITNESSES.json` and `FINAL-VERIFICATION.json` with source/report/verifier pins.
It does not invoke the authority evaluator or recompute acceptance. These checks
are RCA integrity checks, not regression tests or live acceptance.
