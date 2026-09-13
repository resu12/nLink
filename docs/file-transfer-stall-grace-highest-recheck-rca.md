# Fallback stall-grace highest-received recheck — 2026-09-13

## Scope and live evidence

This is the bounded follow-up to live run
`baa10fe1-3d56-419d-b27e-765f8e74be66`, built from HEAD
`dad138284a470342245dc2249c19ca32076c6f12` plus the accumulated working-tree
changes. The relevant runtime path had not changed between that build and the
initial RCA. The scenario was `regular-v4-live-activation-off-on-off-256mb`.

Pinned input:
`artifacts/filetransfer-route-acceptance/activation-retry-live-20260913/20260913T104057Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-retained-log-slice.log`.
It contains 75,644 lines; SHA-256 is
`074335d7cc7c46484b46a39a5e8027e44056972d684d986e3421d82b2ff815fc`.
The complete Coordinator snapshot stream passes validation. Original logs,
receipts, and reports are preserved.

The live transfer completed with matching SHA-256 and the second Coordinator
publication of `file_tuna_v4` on both peers. Overall acceptance nevertheless
remains **FAIL**. Neither completion nor the successful route cycle closes the
unresolved grace obligation or the separate dirty-source provenance rejection.

## Root cause

The first recent-progress candidate is retained line 46100, receiver writer
`p39084rbaaf9e48051343d6`, writer sequence 25345. It holds frontier 5587 in
fallback epoch 2. The exact recent-progress timer did execute; the missing edge
is the following stall-grace recheck after accepted out-of-order data advanced
the highest-received position.

| Retained line | Writer sequence | Evidence |
| --- | ---: | --- |
| 46353 | 25611 | Stall wake 10 armed at frontier 5587 / highest 7621. Start `10:43:45.0176490Z`; deadline `10:43:45.7676490Z`. |
| 46355 | 25613 | Recent-progress wake 141 rechecked with `stall_grace_armed`; its deadline was `10:43:45.0064839Z`. |
| 46409 | 25672 | Accepted sparse write advances highest to 7633; contiguous frontier does not advance. |
| 46425 | 25695 | Accepted sparse write advances highest to 7634; contiguous frontier still does not advance. |
| 46448 | 25709 | Wake 10 is correctly rejected as `stale/frontier_scope_changed` at `10:43:45.7758165Z`, retaining captured highest 7621. |

There is no replacement stall wake or exact new repair dispatch for frontier
5587 before route reactivation. The next initial stall wake is in fallback
epoch 4 at a later frontier. The old callback's rejection is correct; granting
that callback permission to schedule a replacement would violate the authority
contract.

Before the fix, sparse admission updated `PullHighestReceivedChunkIndex`, but
accepted sparse-write completion refreshed only the recent-progress wake. A
stall wake retained its old highest and failed the strict tuple check at its
deadline. Normal grace deferral also did not replace that wake. Thus accepted
tail traffic could remove the only effective timer without closing the gap.

Offline indexed analysis confirms two views of this unresolved episode:

- Stall accounting version 2: Raw/Distinct/Recovered/Superseded/Unresolved =
  **20/20/19/0/1**.
- Recent-progress Raw/Distinct/Recovered/Unresolved = **209/19/18/1**.
- The stall candidate does not have proven source supersession:
  `local_source_supersession_unproven`.

## Bounded implementation

`SessionFileTransferService.PullTransferSessionV6.cs` now refreshes an existing
stall wake from the current accepted sparse-write completion path, under the
service lock, only when highest received has advanced and the gap remains.

The refresh preserves the original stall start and deadline, frontier, exact
Coordinator publication/runtime tokens, route/leg/checkpoint/bridge scope,
terminal generation, and data-session identity/generation. It advances only
the local wake generation and captured highest. Scheduling happens outside the
lock, before any receiver-state send can block, and only after a successful
refresh. A current wake not yet queued may be refreshed too; a mismatched
nonzero scheduled generation is rejected.

Strict callback validation remains unchanged in effect: a replaced generation,
changed highest/frontier, changed authority scope, terminal/pause, or changed
data session rejects the old callback. No stale callback creates a successor.
The normal request path still requires Coordinator repair/control-plane
authorization. The refresh itself neither creates repair authority nor
publishes a route. Its diagnostic is `rearmed/highest_received_advanced` with
`authority_effect=none`.

No production grace duration, retry cadence, throughput policy, queue, chunk
size, wallet, installer, GUI setup, or route invariant changed.

## Separate report and fixture fixes

`AnalyzerOrchestration.ps1` omitted `accounting_version` and `superseded_count`
from four compact text reports and one JSON report. All five now export the
existing versioned reducer values. The regression evaluates those actual
export expressions and the actual report reader on PowerShell 5 and 7. Legacy
reader compatibility, admission, and recovery decisions are unchanged. This
export omission did not cause the missing runtime timer.

An adjacent legacy handoff fixture used equal timestamp/line sort keys for an
original record and a conflicting copy. PowerShell 5 could reorder them and
reject admission before the fixture's intended closure-rejection assertion.
The fixture now uses original input ordinal as the last sort key. This exposed
the same ordering loss in the GUI consumer, which rebuilt shared-correlation
input by enumerating hash-table event buckets. The GUI now restores ledger
sequence order before constructing the shared source index. Both conflicting
copies remain present and recovery must still be rejected; no authority or
recovery rule was weakened.

## Verification and stop boundary

The detailed invocation ledger and offline probe are in
`artifacts/grace-recheck-rca-20260913/RESULTS.md`.
All 20 distinct selected cases pass across the final focused invocations:
six runtime-grace, two compact-report host, five checkpoint-continuity, and
seven grace/admission consumer cases. `git diff --check` passes.
Regressions cover repeated accepted highest advances, unchanged deadlines,
pending scheduling, exact old-wake rejection, continuous rejected frames,
value-equivalent Coordinator adoption, authority/scope invalidation, and the
adjacent blocked-receiver-state and checkpoint-continuity paths.

The next runtime validation requires a **fresh portable rebuild and one new
live 256 MiB test**. Stop before that workflow under the current user boundary.
Do not use the existing portable executable to validate this fix. Run only
`regular-v4-live-activation-off-on-off-256mb`, outside the restricted sandbox,
with explicit escalation and `-StrictCampaignPreflight`; stop if preflight
fails. Do not run the full matrix.

The fresh capture must show the accepted-write successor retaining the original
deadline and the exact Coordinator-authorized follow-up, or identify the new
first blocking edge. The old capture cannot prove the changed runtime or how
the existing fail-closed consumers classify its new successor sequence. No
additional closure rule or recent-progress supersession allowance has been
inferred from this patch. Final clean-source acceptance also still requires a
reviewed provenance gate; no automatic commit or provenance bypass is included.
