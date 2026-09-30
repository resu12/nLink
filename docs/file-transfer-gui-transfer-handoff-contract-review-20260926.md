# Real-GUI transfer handoff contract review

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-selected-terminal-join-20260926.md`.

## Decision

The offline selected-terminal join is not yet connected to the real two-app
GUI transfer. The first missing live dispatch edge is deliberate: the GUI
entry acquires a native frontier owner, but does not pass it to
`Run-ScenarioFileTransferTunaHandoffFallback`. An existing regression test
requires that omission. Do not remove this guard by simply adding
`-FrontierOwner $guiFrontierOwner` to the live call.

The next bounded implementation candidate is a **test-only owned parent-attempt
scope and dispatch bridge**. It must bind the actual selected launch projection
to the original native parent/GUI ownership and exercise the GUI entry/scenario
boundary with headless children. Preserve the ordinary live dispatch, runtime,
acceptance and report behavior. A standalone metadata-equality test would not
close this edge.

This document is a source/contract review, not implementation approval, a live
RCA, or evidence that another live-256 run is ready. Existing process/log
custody can be reused; actual transfer-file custody and full-native analyzer
consumption remain separate gates.

## Current call graph and ownership boundaries

| Boundary | Current source evidence | Consequence |
| --- | --- | --- |
| Parent attempt to wrapper | `Run-FileTransferRouteAcceptance.ps1:80` scopes run/scenario strings; `:9348` builds the actual child arguments; `:9369` enables relay custody for the selected diagnostic path. | No private measured-transfer attempt scope binds the launch fields to custody. Matching labels are insufficient. |
| Wrapper to GUI host | `Run-FileTransferTunaGuiSmoke.ps1:399` can enable frontier capture from an owned relay; `:509` starts the GUI worker with native ownership. | Native GUI-host launch is conditionally reachable. This does not prove ownership reaches the two app launches. |
| GUI entry to scenario | `GuiSmoke-Windows.ps1:42947` obtains the owner and `:42948` initializes its ledger; `:42981` dispatches with `-Context` only. | The scenario's optional owner stays null in the actual live call. |
| Scenario to two apps | `GuiSmoke-Windows.ps1:38450` and `:38451` create requests only with an owner. `FrontierScenarioLaunchGui.ps1:43`–`:45` enforces the live omission; its `:164` invocation supplies an owner only inside the test. | Existing headless tests cover the opt-in function, not live entry-to-scenario propagation. |
| Native files to parent | `LocalGuiWriterParentPreparation.cs:13` retains logs, app manifests and diagnostic payloads; `LocalGuiWriterParentCommit.cs:85` retains provisional custody through commit/exit/take. | Reusable lifetime machinery, not ownership of the sent and received transfer files. |
| Native source to selected analyzer | `LocalRetainedSourceRequest.cs:358` refuses the legacy writer for `frontierRequested`; the test adapter in `NativeWitnessWriterAdapter.cs:42` also rejects that profile. | The offline selected-terminal join cannot be asserted to consume the full-native GUI source. |

Paths above are under `tools/`, `tools/FileTransferOps/`, or
`tests/nLink.SmokeTests.Core/Support/` as appropriate.

The comment at `Run-FileTransferTunaGuiSmoke.ps1:507` saying no live caller
enables native GUI ownership is too broad for the wrapper-to-GUI call graph.
The different comment at `GuiSmoke-Windows.ps1:38447`, saying the live entry
does not pass the scenario owner, remains accurate. Neither comment is edited
in this review; the executable call sites and regression assertion determine
the boundary.

## Proposed measured-attempt scope

Construct the scope at the original parent attempt, from the same selected
scenario and resolved inputs used to launch the child. Carry its immutable,
non-secret projection only through the existing original process-bound
channel. Do not reconstruct authority from a scenario name, environment
strings, a JSON report, an artifact path, or a public copied DTO.

The canonical selected scenario is
`regular-v4-live-activation-off-on-off-256mb`:

| Field | Required binding |
| --- | --- |
| Attempt | Original parent owner, run/scenario identity, attempt index and unique attempt/request binding; identical labels on another owner must fail. |
| Measured transfer | 268435456 bytes (256 MiB), helpee-to-helper, payer helpee. Offline scope tests may validate this projection without transferring that payload. |
| Route exercise | `live-regular-activation-cycle`, `switch-off`, `RegularActivationCycle`, ordered toggles `on,off,on,off`. |
| Measured phase | `measured_live_regular_activation_cycle`, derived consistently with the selected route mode. |
| Required route sequence | `regular_nkn_v4_fast` → `file_tuna_v4` → `post_tuna_fallback_v6` → `file_tuna_v4` → `post_tuna_fallback_v6`. |
| Launch | The actual resolved executable/runtime identity, direction/size/route/proof fields and effective timeouts consumed by that launch; not a second independently assembled argument set. |
| Native lifetime | Exact original request plus bound parent/child PID and creation identity, current owner, one-shot use and cancellation/exit state. Readiness alone grants nothing. |

The parent passes the transfer settings to the wrapper; the wrapper passes
many GUI settings through environment variables, not GUI argv.
`Run-FileTransferTunaGuiSmoke.ps1:2634`–`:2637` carries direction, payload size
and both timeout settings. Verification must cover this projection too.
Wallet contents/passwords and other credentials must not enter scope records,
diagnostic output or seals. There is no new route-authority token: route and
runtime decisions remain coordinator-owned.

## Later gates, not part of the first bridge

1. **Two app writers and roles.** Existing native launch requests bind distinct
   real processes, and `LocalGuiWriterFrontierWriterMatch.cs:17` checks both
   writer closures. Associate helpee/helper roles with those original launch
   requests; neither writer-array order nor matching terminal text proves the
   role or transfer association.
2. **Actual transfer-file custody.** Hold the source identity after payload
   creation and before Send, and acquire the finalized destination through an
   app/transfer-owned identity binding. Keep measurements attached to those
   handles. Current `GetFileSha256Hex` closes its file, while terminal/path
   lookup uses names, sizes and timestamps. Those are candidate checks, not
   continuous custody. A post-finalization acquisition does not prove identity
   during earlier writes. The `terminal_session_id`/`terminal_transfer_id`
   fields in `SessionFileTransferService.cs:24069` are diagnostic correlation,
   not independent authority.
3. **Continuous parent/analyzer adoption.** Retain actual source/destination
   ownership across GUI → wrapper → parent → original analyzer. Use the
   existing provisional-adoption, acknowledgment/commit and normal-exit/take
   lifetime rules; never release the last held copy before adoption or reopen
   by path after a gap. Cancellation, stale owners and abnormal exits must
   retire the candidate without acceptance effects.
4. **Separate transfer payload channel.**
   `LocalFrontierFileManifest.cs:16` limits each diagnostic payload to 262144
   bytes. Those payloads are not the 256 MiB transferred file. Do not relabel
   that catalog, raise its cap, or interpret its log-source byte limit as
   transfer-file support.
5. **Original full-native selected witness.** Prove consumption from the
   original native reader, source origins and selected membership, retaining
   one-pass/EOF, clock, record and code-unit accounting. Do not flip
   `frontierRequested`, reopen the legacy writer, reset budgets, or assume the
   small offline fixture establishes capacity for a live source.
6. **Ordering and acceptance, separately.** Keep the selected-failure
   pre-verdict reservation at `TerminalPreVerdictObserver.ps1:44`. The completed
   offline join is post-report and non-owning. Any pre-verdict promotion still
   needs its own review and approval. SHA OK, fallback completion and a single
   Tuna publication cannot satisfy the canonical second-Tuna-v4 route rule.

## Verification required for the next bridge

Use focused deterministic tests on Windows PowerShell 5.1 and PowerShell 7.
Exercise the actual parent launch projection and original GUI entry/dispatch
seams with native headless children; keep live NKN, paid transport, GUI clicks,
large payload creation/hashing and acceptance evaluation out of this step.

Required positives and refusals:

- The original attempt, launch projection and current native owner reach the
  opt-in test dispatch exactly once, retaining the original output-report tasks.
- Independently vary size, direction, payer, fault, route/proof mode, phase,
  toggle order, timeout, executable identity and attempt identity; reject a
  changed argv/environment projection or another owner with identical labels.
- Reject copied/foreign owners, wrong PID/creation identity, stale/cancelled
  owners, duplicate consumption and child death; preserve normal-exit gates
  and cleanup without launching replacement children.
- Preserve the existing production/default assertion that the live scenario
  call does not supply `-FrontierOwner`. The scope-only test result must not
  claim live eligibility, terminal integrity, route authority or acceptance.

After the focused tests stabilize, run only the directly affected existing
GUI-ledger-entry, scenario-launch and wrapper/relay tests once. No aggregate
suite or full GUI matrix is justified by this source/contract change.

## Review verification and scope

All 1130 pins in the preceding selected-terminal seal matched before this
review. Read-only PowerShell AST checks found zero parse errors in the parent,
wrapper and GUI scripts, and confirmed the actual scenario call has only the
`Context` parameter. These are source/syntax checks, not newly run unit tests
or proof of runtime success.

The incremental review seal is
`artifacts/gui-transfer-handoff-contract-review-20260926/FINAL-VERIFICATION.json`.
It pins this report, the reviewed source and the preceding seal and records
preservation of the 215 pre-existing dirty files. This report adds one dirty
entry. No runtime/test source, acceptance rule, existing report, portable
package, live process, commit or remote branch is changed by this review.
