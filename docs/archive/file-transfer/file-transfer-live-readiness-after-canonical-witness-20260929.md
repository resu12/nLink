# Live-256 readiness after the canonical witness pass

2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved uncommitted work.
Readiness review only; no implementation or live execution in this step.

## Decision

**The canonical offline diagnostic gate is complete. The owned-diagnostic live
run is not ready yet.** Do not repeat the successful canonical fixture or increase
its limits again. The next implementation milestone is the default-off
production ownership handoff, followed by its focused integration verification.
The full GUI matrix is not the next gate.

This is a source-backed integration gap, not a newly reproduced route/runtime
failure. It does not mean the ordinary live scenario cannot launch; it means
that scenario would not exercise the same protected-pair diagnostic chain that
was just verified offline.

## Gates now closed

The fresh run documented in
`file-transfer-pair-evaluation-budget-fix-20260929.md` supersedes the missing
same-pair-witness finding in the September 28 readiness reviews:

- One original 256 MiB source/destination pair, matching SHA-256, and original
  parent/primary custody.
- Three original analyzer evaluations, one native input read and one resolver
  invocation; 28 exact witnesses for the original selected failure.
- `SamePairJoined=true`, `OriginalOutputCommitted=true`, ten original reports
  unchanged, and independent process/file cleanup passed.
- The intentionally failed route verdict remains `FAIL_PROTOCOL_OR_INTEGRITY`
  / `fail`. Diagnostic success does not establish the five-route live cycle.

The resolver-acceptance snapshot was 19,575.126 ms; it is not the final
output-commit clock. The approved evaluation-only 60-second allowance and
1,073,741,824 visits remain unchanged. Producer/source/startup limits remain
unchanged. There is no evidence-based reason to tune them in this review.

## Remaining source-backed prerequisites

| Boundary | Current evidence | Required action |
| --- | --- | --- |
| Parent attempt and GUI dispatch | `Run-FileTransferRouteAcceptance.ps1:9348` builds actual launch arguments; `GuiSmoke-Windows.ps1:42981` still calls the scenario with only `Context`. | Carry the original bounded attempt/role ownership through the real path before enabling scenario ownership. |
| Loader and protected-pair chain | `LocalInputReadLease.ps1` loads native diagnostic support but not the attempt/pair extensions. `FrontierAttemptDispatchLoader.ps1` supplies those test sources under `NLINK_TEST_ATTEMPT_DISPATCH`. | Integrate the needed ownership code into the normal implementation; do not load the test harness in a live campaign. |
| Real receiver acquisition/export | `SessionFileTransferService.NativeDestinationEvidence.cs:11` and `NativeDestinationGuiHandoff.cs:20` provide the internal primitives. Their call sites are in tests, not the production app path. | Request custody on the original Service-owned destination and adopt its finalized protected handle into the real GUI chain without a path reopen or lifetime gap. |
| Original primary consumer | `AnalyzerOrchestration.ps1:9185` already performs optional native resolution after reports. Actual-pair binding and the same-pair assertion remain test-only. | Join the original protected pair to that same original evaluation/witness/output lifetime; preserve conditional resolution and cleanup. |
| Source/package provenance | Worktree has 618 pre-existing dirty entries. Default portable executable remains the September 13 build. | Review/checkpoint the intended changes, obtain a clean acceptance source state, then rebuild and pin the portable runtime/harness/bridge provenance. |
| Live environment | No network, bridge, wallet or GUI preflight ran in this review. | Run strict escalated preflights immediately before the authorized targeted attempt; stop on failure. |

The existing launch regression explicitly rejects a live call containing
`-FrontierOwner` (`FrontierScenarioLaunchGui.ps1:56`). Appending that parameter
alone would bypass a deliberate boundary, not finish integration. The test
profile currently substitutes the owner into a checked in-memory dispatch
expression (`FrontierAttemptDispatchGui.ps1`), which is not production wiring.

The source-cleanliness condition is substantive: the acceptance validator adds
`source_dirty` when either initial or final source is dirty
(`FileTransferRouteAcceptanceEvidence.ps1:2046-2054`). Hashing a dirty snapshot
does not satisfy that existing rule. Do not hide changes, falsify the manifest,
relax provenance, or commit unrelated work automatically. A reviewed local
checkpoint is a later explicit action; no push is needed for this review.

Default executable: `artifacts/portable/nLink/win-x64/nLink.exe`, 135,108,819
bytes, last written `2026-09-13T18:02:18.3413197Z`, SHA-256
`b012cdf4b4d9b620c1de459679a3551e4d704023d4e399ff907b9090fea84ae7`.
The newly built Core test DLL is not a portable-app rebuild.

## Smallest next implementation scope

Start with **production ownership integration**, default-off under the existing
explicit diagnostic capture request and exact canonical scenario. Use the
already-tested custody chain as the reference; do not invent a new general
channel, broaden supported scenarios, or enable paid/live execution while
developing it.

1. Bind the original parent attempt to the exact argv/environment projection
   actually consumed by wrapper and GUI: executable/runtime identity, size,
   direction, payer, route/proof mode, fault, ordered toggles and timeouts.
   Preserve process/creation identity, original request, one-shot use and both
   peer roles. Matching strings or environment flags alone grant no custody.
2. Wire the actual source and destination owners through GUI, wrapper, parent
   and original primary. Keep transfer files separate from the bounded native
   diagnostic catalog. Retain the previous owner until adoption/acknowledgment
   completes; preserve abnormal-exit/cancellation retirement. Do not promote
   test fault schedules, reflection probes or test-only partial loaders.
3. Keep the native diagnostic post-report and conditional on the primary's own
   supported unresolved failure. Reuse its original input, completed views,
   selected failure and resolver exactly once. No new authority evaluation,
   failure replacement, report rewrite or acceptance change. If a live attempt
   selects no eligible failure, honest diagnostic omission is valid; do not
   reproduce the offline injected delivery schedule on the live network.

Verify each changed edge with directly affected tests first. The acceptance
criteria for this integration are execution through the normal loader and real
launch/finalization code, exact original ownership, rejection of changed/copied/
foreign/repeated/retired owners, unchanged default-off behavior and original
reports, and independent cleanup. Headless native children may substitute for
UI interaction in focused controls, but must not replace the ownership edge
being claimed. Run only affected lanes after stabilization, then one integrated
ownership check before considering the live gate. The existing successful
canonical fixture is retained evidence, not an automatic rerun requirement for
unaffected code.

## Diagnostic readiness is not alternative acceptance

Earlier readiness reports grouped selected-failure accounting with live
ownership. Keep the distinction explicit now: the native branch at
`AnalyzerOrchestration.ps1:9185-9204` is already after original reports and cannot
change acceptance. Completing a pre-verdict alternative acceptance algorithm
is **not** required to collect diagnostics under the existing strict contract.

Keep `TerminalPreVerdictObserver.ps1:44` and the current selected-failure writer
reservation. Do not subtract a selected failure from aggregate counts or promote
the test-only terminal shadow. Any future relaxation still requires separate
review/approval and complete independent-failure accounting. That is outside
this proposed diagnostic integration and is not a reason to delay it with
another acceptance prototype.

## Frozen candidate for the later live gate

After integration verification and a reviewed clean checkpoint, rebuild portable
from that exact source. Resolve and record runtime, harness, bridge and sidecar
hashes; verify the executable freshness gate. Do not record wallet secrets.

Use `tools/Run-FileTransferRouteAcceptance.ps1`, matrix mode
`phase5-analyzer-gui-acceptance`, only
`regular-v4-live-activation-off-on-off-256mb`, with `StrictCampaignPreflight`
and `CaptureLocalPublicationDiagnostics`. The candidate is one attempt:
`FallbackMaxAttempts=1`, `SetupOnlyRerunLimit=0`, `GoodputOnlyRerunLimit=0`.
Keep current scenario/timeouts and correctness/performance policy unchanged.
Final invocation and environment must be verified after implementation, not
treated as executable authorization from this review.

Run outside the restricted sandbox with explicit escalation. No fake, dry-run,
skip-preflight or quarantine bypass. Preserve the required sequence Regular4
-> Tuna4 -> fallback6 -> Tuna4 -> fallback6, including the second
coordinator-published `file_tuna_v4`. SHA OK, fallback completion and diagnostic
output are insufficient. Correctness remains separate from goodput. Stop at
the first real failure; no automatic retry or full matrix.

## Verification and preservation

Entry verified all 8,488 pins and 618 dirty entries against
`artifacts/canonical-pair-evaluation-budget-native-20260929/FINAL-VERIFICATION.json`
(SHA-256 `f57dd21fbb1f3ea716940be15f8d2e1a46114e369c4a24d405263b4b24fe7e84`).
The original HEAD/branch, source, binaries and successful/failed captures remain
unchanged. Read-only AST/source checks and executable hashing are recorded in
`artifacts/live-readiness-after-canonical-witness-20260929/` with a final seal.
No tested code changed, so no tests or aggregate lanes were rerun. This report
adds one dirty entry; no build, preflight, live transfer, commit or push occurred.
