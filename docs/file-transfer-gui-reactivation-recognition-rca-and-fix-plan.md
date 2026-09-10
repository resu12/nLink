# GUI second-Tuna recognition: RCA and bounded fix plan

Date: 2026-09-10. Source HEAD: `9b135032417d6fc3c8e1d139205d0312eeb241dc`.
Status: bounded harness/test implementation completed and reviewed for the approved scoped commit on 2026-09-10. No new live attempt or report is authorized.

Implementation verification: 11 distinct focused xUnit cases have passing final results, including a 45-case proof/diagnostic matrix on each PowerShell host and the asymmetric four-step deterministic cycle. Final immutable replays on PS5.1 and PS7 recognize epoch 3 before terminal, have identical snapshots, and preserve terminal-first action rejection. Details and development failures: `artifacts/validation/consolidation-bounded-20260907/implementation-gui-route-pair/RESULTS.md`. The RCA evidence and source line references below describe the original pre-fix HEAD, not a new accepted live run.

## Finding

The first missing proof edge in the latest run is **the GUI's cross-direction join**, not a missing second Tuna route publication or missing payload delivery. Both direction-local coordinator proof chains are complete before completion, but `Get-TunaGuiFaultAdmissionProjectionKey` includes direction-local counters in a key that `Update-TunaGuiFaultAdmissionAuthority` requires to be identical across peers.

Latest immutable input: `artifacts/filetransfer-route-acceptance/20260910T155044Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-gui-log-ledger-slice.log`.
SHA-256: `6175454f09a1203c19cff84979041e8dc1b5ef4af4f195bd2a30ba74c30c9ee9`.
This capture was built and run at the pre-fix HEAD named above. No stale artifact or sandbox network failure was used to justify a runtime change.

The run remains **FAIL**: 256 MiB completion and matching SHA are verified, but the fourth toggle/final fallback never occurred. The observed raw route sequence is RegularV4/0, TunaV4/1, FallbackV6/2, TunaV4/3. The GUI milestone sequence stops after FallbackV6/2. No historical acceptance result is upgraded.

## Exact proof and reproduction

Line numbers below are one-based full-capture lines. The eight edges selected by the unchanged production tracker before line 151250 are:

| Direction | Commit | Accepted publication | Applied disposition | Payload observation |
|---|---:|---:|---:|---:|
| Outbound | 151166 | 151173 | 151180 | 151207 |
| Inbound | 151198 | 151224 | 151240 | 151230 |

Outbound writer: `p53936re1074251c4904837`; inbound writer: `p10676rf51f809a77924bf2`.
The commits have distinct, non-redacted schema-2 transition authority IDs. Each disposition matches its own direction's authority ID and transition generation. Each publication and payload matches its own direction's complete target tuple. This is stronger than merely finding `route_selected` or a successful send.

The source's combined-key comparison differs in exactly these components:

| Key field | Outbound | Inbound | Domain |
|---|---:|---:|---|
| Target runtime generation | 4 | 11 | Direction-local |
| Target leg generation | 4 | 11 | Direction-local |
| Target publication generation | 4 | 11 | Direction-local |
| Source bridge-recovery generation | 1 | 8 | Direction-local |

Target route/protocol/epoch are `file_tuna_v4/4/3` in both directions. Target terminal generation is 0, target commit generation is 2, source transport epoch is 1, checkpoint request is `(none)`, and source data-session generation is 1 in both. The two commits also carry the same coordinator-emitted fallback `pair_key`: `sess_[redacted]|[redacted]|post_tuna_fallback_v6|6|2|1|(none)`.

At 16:05:23 UTC, both second-Tuna routes and both local proof chains exist. Inbound terminal is line 172380 and outbound terminal is line 172406, at 16:05:41 UTC. The replay's completed-terminal pair is recognized at line 172415. Thus the claim that no second activation occurred before completion is false for this capture.

Offline diagnostic `artifacts/validation/consolidation-bounded-20260907/rca-gui-reactivation-recognition/Replay-Recognition.ps1` imports unchanged production function ASTs, not the GUI entrypoint. It uses the real RollingMemory range visitor, including coalescing, and starts at the actual reactivation claim's synchronized bookmark 64145. No captured line or authority is manufactured or changed.

Results on Windows PowerShell 5.1 and PowerShell 7 are identical:

- At end bookmark 151250: two complete local proofs; no combined projection; no terminal.
- At 152500: same result, with subsequent payload/disposition evidence.
- At finalized end 173620: two local proofs; no combined projection; completed terminal pair present.

An additional check of the production C# append-only selector over the immutable prefix retained all eight exact proof lines once, with coalescing enabled. The selector therefore does not explain the missing join. This is an offline recognition reproduction, not a replay of actual UI scheduling or a new route-acceptance/report run. It cannot guarantee a future fourth click will beat completion.

## Source causality

1. `tools/GuiSmoke-Windows.ps1:33566`: the projection key removes direction from the exact local target key but leaves local runtime/leg/publication/commit/terminal counters, then appends source incarnation fields.
2. `tools/GuiSmoke-Windows.ps1:33651`: complete inbound/outbound local proofs are combined only when that key matches exactly. The fallback consumer repeats the same comparison at line 33709.
3. `tools/GuiSmoke-Windows.ps1:33924`: the waiter continues without a combined projection and correctly fails closed once terminal completion is observed.
4. `tools/GuiSmoke-Windows.ps1:36712`: the immediate-reactivation catch probes completion and emits `terminal_before_same_transfer_reactivation_immediate_probe` without establishing that temporal ordering or preserving the activation-wait cause in that milestone. This is a secondary diagnostic misclassification, not proof of runtime non-activation. The original thrown error was not retained, so its exact value is not asserted here.

The domain distinction is intentional in Core: `FileTransferCoordinator.cs:24263` advances a local runtime generation during leg replacement; line 24485 advances it on route transition; line 7653 issues the next publication from local state. `BuildFallbackTailYieldPairKey` at line 14967 omits local leg/bridge generations while the owner key includes them. Existing tests `FallbackTailYield_PairScopeExcludesDirectionLocalBridgeGeneration` and `FallbackTailYield_InitialPeerProofRetainsDirectionLocalRecoveryGenerations` explicitly preserve this separation.

The existing GUI test's “mutated cross-direction target tuple” assertion at `FileTransferGuiHarnessScriptTests.cs:3215` actually changes only the inbound publication runtime generation, leaving its own commit unchanged. That is a valid **within-direction mismatch** rejection and must continue to pass. It does not test two internally valid but asymmetric direction-local tuples.

## Approved bounded implementation contract

Assumption: the repair is confined to GUI evidence recognition and its immediate failure diagnostics. Coordinator state transitions and transport behavior are unchanged.

### 1. Separate exact local proof from shared route pairing

- Keep `Get-TunaGuiFaultAdmissionTargetKey` and the local commit/publication/disposition/payload requirements strict. Do not normalize counters, rewrite evidence, substitute `route_selected`, or accept send/bridge observations as authority.
- Define a named shared projection scope rather than stripping direction from the owner tuple. Require the same bounded attempt/session/transfer, target route/protocol/live epoch, transition kind and shared source route/protocol/live epoch. Preserve source transport/checkpoint correlation where the coordinator's shared-pair contract requires it.
- For `PostTunaFallbackV6ToTunaV4`, require the existing coordinator-emitted fallback pair key to be present, equal, and consistent with that shared source scope and the current fallback/reactivation window. Do not reconstruct a missing pair key, infer it from matching counters, or require initiator and responder local authority IDs to be equal.
- Runtime, leg, publication, commit, bridge-recovery and data-session generation counters are not cross-peer identity. Preserve them in their local proof records and diagnostics. The live action remains nonterminal; this does not permit terminal-generation mismatches to authorize actions.
- Because the projection helper has Tuna and fallback consumers, review and test both under the same domain rule. Do not leave final fallback dependent on equality of these same local owner counters. Keep initial RegularV4 activation behavior covered; it must not be required to supply a fallback pair key it does not have.
- Preserve the action/projection generations, minimum bookmarks, direction checks, cursor behavior, current-scope revalidation, idempotent action claim and terminal-first guards. A valid historical proof must never cause a click after a terminal has been observed.

Ordinary log session/transfer/pair strings are redacted. Equal redacted text alone is not a new globally unique identity. The implementation must retain the existing attempt/writer/UI-transfer scoping and reject an ambiguous candidate set. If that bounded association cannot be demonstrated with existing evidence, stop for an explicit evidence-binding contract decision; do not silently broaden log-derived authority or introduce a runtime/schema extension in this slice.

### 2. Correct the immediate-reactivation diagnostic

Preserve the original activation-wait failure and report whether local proofs were incomplete or complete-but-unpaired. Use a terminal-before-activation label only when established; otherwise describe completion while authoritative GUI recognition was unresolved. Do not reorder terminal/action safety guards, synthesize a recovered milestone from raw route lines, or turn a failed route-cycle into a pass.

### 3. Verification gates

Before changing production code, add a focused regression with the captured 4/11 target counters and 1/8 bridge generations. Both direction-local proof chains must be internally valid, share the coordinator fallback pair and window, and occur before terminal. Demonstrate the current missing combined projection, then require the proposed join to recognize epoch 3.

Focused negative controls must retain rejection for:

- An altered local publication/payload target tuple, missing/rejected disposition, stale authority/generation, or missing payload.
- Different shared session/transfer, source/target route or protocol/epoch, transition kind, fallback pair, required shared transport/checkpoint scope, or ambiguous writer/transfer association.
- Evidence before the minimum bookmark, replaced action/projection windows, a repeated claim, or terminal completion before action admission. If proof and terminal arrive in one update, no late click is allowed.

Run the affected GUI tracker/key, action-claim, terminal-awareness and immediate failure-classification tests on PS5.1 and PS7. Use the existing generation-domain Core tests as contract controls; no Core/runtime suite or runtime rebuild is needed solely for this harness change. Cover both Tuna/fallback callers and the full deterministic on/off/on/off fixture without assuming symmetric local counters. Run at most one broader relevant contract lane once stabilized, not repeated aggregates.

Finally, replay the exact immutable input under the fixed recognizer and require the pre-terminal second-Tuna projection while keeping the finalized capture fail-closed for live actions. This only verifies recognition. Preserve the original historical FAIL, missing final fallback and immutable capture hashes.

## Boundaries and next decision

Expected production scope: `tools/GuiSmoke-Windows.ps1`; focused tests in `tests/nLink.SmokeTests.Core/FileTransferGuiHarnessScriptTests.cs`, plus a directly affected summary-writer test only if its diagnostic contract changes. No Coordinator/Core/Infra/Go behavior, protocol, mandatory catalog/schema, queue/concurrency, chunk/retry/timeout, wallet, installer, or GUI fault-timing change.

The RCA stage performed no implementation, commit, portable rebuild, preflight, GUI launch or retained report. The subsequently approved implementation changed only the GUI harness, focused tests and documentation; ordinary test compilation was performed. All three unrelated dirty files remain byte-for-byte preserved. The eight production attempts and three standalone reports remain consumed. A later targeted live validation needs separate approval and allowance, focused tests first, a fresh portable rebuild and escalated preflight; the full matrix is not the next gate.

**Reviewed handoff:** no blocking code issue was found in the bounded pre-commit review; the verified source hash and dual-host receipts still match. Commit only the harness, focused tests and this document. A fresh targeted live attempt remains a separate authorization and allowance decision. The historical run remains FAIL because no final off/fallback occurred.
