# Live-256 readiness after the native witness milestone

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Review only: no code, acceptance, build, test, analyzer/resolver, transfer,
commit or push action.

## Decision

**Not yet ready for the targeted live-256 owned-witness integration.** The small
failure-bearing native-witness milestone is now closed. Do not describe it as
still missing, retry that successful profile, or reopen its completed identity,
marker-retention and watchdog work.

The first remaining integration gap is joining a genuine primary-selected native
witness to the **same protected canonical 256 MiB source/destination pair**.
The successful canonical-pair run and successful small-witness run exercise
different transfers and cannot supply that join by combining their artifacts.
This is an existing integration requirement, not a new runtime defect or an
additional route-acceptance rule.

## Updated gate inventory

| Gate | Verified evidence | Remaining boundary |
| --- | --- | --- |
| Small failure-bearing native witness | Latest 168 KiB run: one resolver call, 41 exact witnesses, original output committed, report parity and cleanup passed. | Closed for the small profile. |
| Canonical actual-file pair to primary | Earlier canonical-fallback run: original 256 MiB pair, parent 2/2/1 guards, three evaluations/one read, matching SHA and cleanup. | That run has zero diagnostic receipts; same-pair witness consumption is unverified. |
| Selected-failure accounting and live ownership | Current shadow reserves the original one-shot writer; default live dispatch still supplies only Context. | Separate consumer/enablement integration remains. |
| Portable package and strict live preflight | Default executable is still the September 13 build. | Rebuild/provenance and escalated readiness checks are required immediately before an authorized targeted run. |

The small observation remains `PublishedWithinObservationBound`, with original
primary `FAIL_PROTOCOL_OR_INTEGRITY` / gate `fail` and all three diagnostic
authority flags false. Its deliberately late profile passes by preserving the
unresolved failure and delivering its diagnostic witnesses, not by accepting
the route. The older canonical result remains `INCONCLUSIVE` / incomplete proof;
neither capture demonstrates the second Tuna publication.

Original runs:

- Small witness:
  `artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-33ffeb5cb1794f5580227e07ac43a574/`.
- Canonical actual pair:
  `artifacts/origin-capacity-implementation-20260927/pwsh-canonical-04bc0771d73648bdba12c8211a483448/`.
  Original stdout still says `full_lines=73713; diagnostic_receipts=0`.
  This is preserved historical custody evidence, not fresh validation of later
  code changes or grounds for a runtime RCA.

## Source-backed reasons this is not a flag-only rerun

1. `FrontierAttemptDispatch.ps1:8-12` explicitly excludes wrapper, parent and
   primary transfer-pair hooks from both small diagnostic profiles. The passing
   late-publication test supplies those arguments as empty. Removing this guard
   would expand the reviewed profile, not complete the missing ownership proof.
2. `FrontierAttemptNknCarrierTests.cs:147-148` rejects the scheduled fault when a
   full-size native case is present and requires the small schedule for late
   publication. Its small source is a MemoryStream, not the fixture-held
   canonical file. The receiver's actual destination handoff and the canonical
   retained source must remain on one original transfer.
3. `AttemptOfflineFrameSchedule.cs:9-22,42,49-70` is a Debug single-segment,
   eight-ordinary-frame profile, with explicit indices 0-7 and bounded holds.
   Scaling its payload or allowing index 8 does not establish a safe full-size
   schedule. The remaining stream, packet geometry, nonterminal frontier and
   all-copy publication timing need an explicit bounded design; no production
   batching/deadline or witness-cap increase is justified here.
4. `FrontierAttemptPayloadPrimary.cs:271-315` binds the held transfer pair to the
   original evaluation objects and exposes count/hash/currentness, but that
   binding does not consume the original selected native witness. The paired
   probe checks binding and cleanup, not a witness-to-pair association.
   Stored session/transfer fields at lines 56/69 are evidence to bind through
   original ownership, not permission to match arbitrary artifact labels.
5. `AnalyzerOrchestration.ps1:9185-9196` still lets the original primary select
   the supported unresolved failure before its single optional resolver. Keep
   that ordering and its 1-64 exact witness requirement. A probe must not select
   a substitute failure or call another evaluator to force eligibility.
6. `TerminalPreVerdictObserver.ps1:44` remains a test-only selected-failure writer
   reservation. Do not remove it or let a shadow consume the writer before the
   original witness branch. It is not itself evidence of a production fault.
7. The unique live call is in **`tools/GuiSmoke-Windows.ps1:42981`**, not the outer
   `Run-FileTransferTunaGuiSmoke.ps1` wrapper. It still passes only `-Context $ctx`.
   The normal `LocalInputReadLease.ps1` loader includes native diagnostic support;
   the additional attempt/actual-pair APIs come from the test-only
   `FrontierAttemptDispatchLoader.ps1` and `NLINK_TEST_ATTEMPT_DISPATCH`.
   Adding `-FrontierOwner` alone would not establish production pair custody.

## Smallest next step

Prepare a feasibility-backed **canonical same-pair witness integration contract**,
before implementation or another transfer. Reuse the existing canonical source,
destination, parent and evaluation custody, and the proven primary-owned witness
path. Specify the bounded full-size fault geometry and how the original witness
is associated with that same evaluation-owned pair before either retires.

Its success criteria must include original session/transfer and writer lineage,
exact held files and canonical SHA, original evaluations/views and selected
failure, one resolver call, 1-64 native witnesses, unchanged reports/authority
flags, and independent cleanup. Preserve existing foreign/copy/repeated/retired
owner rejection. No path reopening, borrowed small receipt, fabricated failure,
clock adjustment or additional authority evaluation may fill the join.

Do not assume a viable full-size schedule from this review. If a bounded design
cannot meet the current geometry, timing, custody and capacity limits, state the
precise constraint for a contract decision instead of retrying for luck. Existing
small positive/negative and canonical custody controls remain valid within their
own scopes and should not be rerun without an affected change.

After that integration: finish selected-failure accounting/live dispatch
ownership, then package/provenance and strict escalated preflight. Only then run
the separately authorized `phase5-analyzer-gui-acceptance` case
`regular-v4-live-activation-off-on-off-256mb` with `StrictCampaignPreflight`.
Require Regular4 -> Tuna4 -> fallback6 -> Tuna4 -> fallback6, including the second
coordinator-published `file_tuna_v4`; no fake/dry/skip or failed-preflight bypass.
Correctness remains separate from goodput. These are remaining milestone groups,
not a promised number of turns/tests. The full matrix is not the next gate.

## Verification and preservation

All 3,605 prior pins and 532 dirty entries matched at entry. Five relevant
PowerShell scripts parse without errors; AST inspection confirms the unique
live call and its sole Context parameter. The default portable executable is
135,108,819 bytes, last written `2026-09-13T18:02:18.3413197Z`, SHA-256
`b012cdf4b4d9b620c1de459679a3551e4d704023d4e399ff907b9090fea84ae7`.
No network/bridge/wallet readiness claim is made.

No tested source changed, so no tests or aggregate lanes were rerun. Only this
handoff and ignored review evidence/seals were added (533 dirty entries). Prior
sources, binaries and captures remain unchanged. Evidence and final independent
check: `artifacts/live-readiness-after-native-witness-20260928/`.
