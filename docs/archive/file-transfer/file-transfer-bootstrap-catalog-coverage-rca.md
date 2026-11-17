# Bootstrap catalog coverage: RCA and bounded fix plan

2026-09-15. Discovered by the A8b5 writer/emitter compatibility gate. No fix to
runtime, analyzer disposition or historical verdicts is implemented by this note.

## Current failure and first missing check

The completed compatibility run passed 58/66 rows with exactly the eight failures
below; all four final native writer rows passed. Fresh modeled inputs from the
current production emitter declare the
`...-source-callback-v1-repair-owner-v1-activation-retry-v1` catalog, hash
`6f8667e92dae781e679bbe6f59fbd4f3d6284cd893ad2f643a9e47a21d451b00`.
The broader compatibility run reports two distinct problems:

1. Four projected-exchange rows stop at `correlation profile not declared`.
   `TestData/BootstrapCorrelationEvidence.ps1` pins the older bootstrap-initiation
   profile while its input comes from the current activation-retry emitter.
   This is a stale test-profile assertion, not permission to weaken ownership.
2. Four source-hold rows across PowerShell 5.1 and 7 fail real obligation checks.
   An invalidated source hold expects one unresolved legacy obligation but gets
   zero; the committed-cleanup `missing_hold` mutation is also waived.
   These assertions should not simply be changed to expect zero.

The first missing production check is in
`Test-FileTransferFallbackV6BootstrapCorrelatedEpisodeConsumed`, in
`tools/FileTransferFallbackV6BootstrapDirectionCorrelation.ps1` around line 385.
The source-hold consumption predicate is invoked only for the initiation and
source-callback catalog versions. The outer catalog capability predicate already
admits the later repair-owner and activation-retry versions, and their operation
validator also recognizes inherited source-hold semantics. Those later versions
therefore bypass the required source-hold consumption check and can incorrectly
select the completed exchange as owner of a legacy diagnostic.

## Evidence and attribution

Latest relevant modeled inputs include:

- `artifacts/bootstrap-ownership-fix-20260912/0785f72cef114ffa8e186b7ed2d0ef83/hold_invalidated.log`
- `artifacts/bootstrap-ownership-fix-20260912/2e19a1fbafd04dfd987170307c8759e1/hold_invalidated.log`
- `artifacts/bootstrap-ownership-fix-20260912/eb8950b971a2486cb9467b00d62a3a68/committed_cleanup.log`
- `artifacts/bootstrap-ownership-fix-20260912/954149a6012b4990b300bbac9ded0013/committed_cleanup.log`

Despite the fixture directory's historical name, these inputs were freshly emitted
on 2026-09-15 during `native-writer-queue-compatibility.trx`. The current emitter
catalog/version/hash and the two-version source-hold guard are identical in HEAD
`2691fc70f98d668bae943ebd41c6f8540156ec57` and the working tree. Neither analyzer
selector nor the two failing fixture scripts has a working-tree change. This
establishes that the gaps predate A8b5; they are not caused by its optional writer
registration or reference-keyed queue association.

A calibration replay of the older source-callback-profile input at
`artifacts/bootstrap-ownership-fix-20260912/bc373634e2414dfd8aa2a848e4b4beba/hold_invalidated.log`
passed the existing ownership script on PowerShell 7. That script also regenerated
its derived `ownership-gui.log` beside the fixture. The original captured input
was not edited and this replay is not a live-result reclassification. The RCA
above relies on current inputs and source inspection, not on treating that older
profile as current runtime evidence.

## Bounded fix plan

1. Extend the source-hold consumption gate to the explicitly supported repair-owner
   and activation-retry catalog versions, matching the existing initiation-operation
   validator's finite version set. Preserve the old correlation-only behavior and
   fail-closed treatment of unknown/invalid catalogs; no prefix-based future-version
   acceptance or new authority evaluation.
2. Correct the projected-exchange fixture's production-profile assertion to the
   actual current catalog. Preserve frozen historical catalog hashes and verify
   its full/compact/catalog comparisons against the matching catalog definition.
   Do not merely strip the assertion or weaken source-hold expectations.
3. Add focused version-coverage controls using valid corresponding catalog inputs:
   invalidated/missing hold remains unresolved; an exact committed consumption may
   own only its matching legacy diagnostic. Keep independent route/artifact failures
   intact and test unknown catalog rejection and original-view parity.
4. Rerun the eight failing rows and directly affected ownership/consumer tests on
   PowerShell 5.1 and 7. Do not repeat unrelated catalog/native writer lanes or run
   live-256 just to confirm this deterministic analyzer fix.

This changes correctness selection in the analyzer, so it is a separate bounded
change from A8b5. Resolve it before treating the wider compatibility gate as green
and before proceeding to the marker/drain implementation. Do not backfill or
upgrade historical live results. No queue, throughput, retry, transport or runtime
authority changes are called for by this RCA.

## Implementation and verification: 2026-09-16

Implemented the finite four-profile source-hold gate and corrected the fixture to
the current activation-retry profile. Frozen catalog hashes and extension parent
links remain checked. Version controls validate full streams under each supported
initiation-capable catalog before ownership selection; missing-hold predicate
mutations are labeled separately. Unknown catalogs, correlation-only compatibility,
independent failures and full/compact behavior remain covered.

All 12 distinct targeted rows now have passing latest results on PowerShell 5.1
and 7, resolving the original eight failures. The initial run was 8/12 because a
new unknown-version test expected the wrong rejection reason. After correcting
that test assertion, the six affected source-hold rows passed 6/6; successful
projected/consumer rows were not rerun. See
`artifacts/bootstrap-catalog-coverage-20260916/RESULTS.md` and its two TRX files.

The historical 58/66 result is retained unchanged; no fresh all-66 pass is claimed.
This bounded compatibility blocker is resolved. Marker/drain ordering and native
payload/source-file custody remain unfinished next gates. Runtime authority, live
activation and historical verdicts are unchanged. No live test, commit or push.
