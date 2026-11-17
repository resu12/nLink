# Canonical reactivation-window review and bounded next plan

Date: 2026-09-28. Review only: no source/test edits, analyzer rerun, build,
portable package, live GUI/NKN run, acceptance change, commit or push.

## Decision

**Do not patch GUI timing or runtime reactivation from this offline capture.**
The fixture deliberately performs Regular v4 -> Tuna v4 -> fallback v6 and
then completes. It never attempts the next on/off pair. Its incomplete
RegularActivationCycle result is therefore expected, not evidence that the
real GUI lost a reactivation race.

The passing custody/analysis fixture remains valid. The next integration gate
is a genuine same-transfer native frontier receipt/marker and an eligible
primary-selected witness. Live enablement and complete route-cycle proof remain
separate gates. Another unchanged canonical retry or live-256 run will not
prove those missing ownership transitions.

## Evidence and freshness

Baseline: v0.7.0, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, plus the
existing worktree. All 1,111 pins and all 453 dirty entries match the previous
seal, SHA-256 `f253d611bc4155bad605bea48791488a62fd7259ceb5d9105000ea565b60fef9`.

Latest relevant artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-04bc0771d73648bdba12c8211a483448`.
It was rebuilt immediately before the previous turn's single offline run.
The retained source has 73,713 lines and SHA-256
`488056bd6d81054fd82c92d75fa9d033d857207f3e7d8f6c8b1197439dd97680`.
No older live capture is used to justify a current runtime change.

This review reads immutable source/report records and source code only. It does
not rerun the authority evaluator or report classifier. Its diagnostic line
references are not primary-owned native witness capabilities. The previous
primary's zero diagnostic receipts are not upgraded by this inspection.

## Root cause of this fixture's missing second activation

The ordinary scenario identity still requests RegularActivationCycle, but the
offline test substitutes the action implementation:

- `Support/FrontierAttemptDispatchGui.ps1:71` validates the original scenario
  arguments using a test cycle function.
- `Support/FrontierAttemptSourceGui.ps1:29` replaces that function with held
  source creation/protection and sender/receiver delivery, not the real GUI
  toggle loop. Its line 76 invokes `FrontierAttemptSenderReadGui.ps1`.
- `FrontierAttemptCrossProcessReceiveTests.cs:125` selects
  `RunCrossProcessCarrierAsync` for this carrier profile.
- In `FrontierAttemptNknCarrierTests.cs`, line 222 requests initial Tuna,
  line 226 calls StopAccelerationAsync("runtime_disabled"), and line 251
  releases data after both peers reach fallback. Line 256 waits for terminal;
  line 300 requires final post_tuna_fallback_v6/protocol 6. There is no second
  negotiation request or final off action in that implementation.

The actual GUI path is different: `GuiSmoke-Windows.ps1:37182` claims the
reactivation action from its tracker, line 37198 invokes Unlock-TunaPayers, and
the following code records invocation. That body is not executed by this
offline cycle substitution. Adding a second action to the existing fixture
would change its intentional fallback-completion contract; it is not a small
fix to a reproduced GUI timing bug.

## What the retained records establish

One-based retained-source coordinates, inspected as diagnostics:

| Line | Observation |
| --- | --- |
| 687 / 726 | Inbound/outbound fallback epoch 2 recovered |
| 1388 / 1390 | Outbound pairing accepted and paired yield granted |
| 1396 | Coordinator retry request queued, contract generation 1 |
| 2822 | Retry slot deferred for backoff |
| 4259 | Retry slot deferred as ineligible, authority_effect=none |
| 73649 | Outbound transfer completes on fallback epoch 2 |
| 73650 | Retry intent rejected because its scope was invalidated |
| 73675 | Inbound transfer completes on fallback epoch 2 |

No literal GUI prearm/action/toggle record or post-fallback wake/authority-grant/
dispatch-settlement record occurs in this retained source. The original report
still says INCONCLUSIVE, incomplete_proof, zero hard failures, and failed live
route-epoch proof. No second Tuna route or final fallback is inferred.

The slot's ineligible reason alone does not expose which eligibility predicate
failed. `NknSignalingTransport.Acceleration.cs:31039` includes disposal, lane
type, user-stop state and session eligibility. The fixture's explicit stop and
absence of a second request explain its coverage boundary, but this review
does not claim an unrecorded runtime predicate value.

## Why the backlog variant is not a demonstrated cause

`FileTransferSoak/ArtifactWriters.ps1:11867` defines the reactivation-window
correlator. Its qualifying chain is epoch-2 recovery, paired yield, a queued
request, backoff/ineligible decisions, and terminal completion without a
matching earlier wake. At line 12186 it assigns the fixed variant
`fault_admission_tracker_backlog_delayed_prearmed_unlock_past_terminal`.

The predicate does not require a GUI tracker backlog measurement or an actual
prearmed GUI action. `FileTransferOps/AnalyzerOrchestration.ps1:5085` invokes
this correlator. Thus the reported absence of reactivation is supported, while
the more specific causal wording is not established for this fixture. Preserve
the existing report and strict failure; do not rewrite history or weaken its
route-cycle obligations. A diagnostic-label revision would be separate scope,
not a prerequisite runtime fix inferred from this artifact.

The older 2026-09-10 GUI recognition RCA concerned a different live capture
that actually contained the second Tuna publication. Its already implemented
cross-direction recognition fix must not be reapplied to this different case.

## Remaining native receipt/witness gate

Both original operational log and retained source contain zero
`filetransfer_frontier_payload_marker` records and zero records with the
`filetransfer_fallback_frontier_recent_progress_grace_recheck` event prefix.
The original primary reports diagnostic_receipts=0. The original operator
report gives zero raw/unresolved recent-progress-grace-recheck failures.
These observations do not establish whether some unrecorded producer admission
was attempted; they do rule out blaming loss of an existing source marker on
retention in this capture.

`LocalNativeFrontierWitness.ps1:54` accepts only the original selected
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing` failure;
line 95 further requires its original frontier_stalled receiver-state event.
The current selected GUI-window failure is not that family. A successful
fallback transfer, completed receipt or payload SHA cannot substitute for the
missing supported candidate and native diagnostic receipt.

The original service admission at
`SessionFileTransferService.FrontierServiceAdmission.cs:24` requires a current
coordinator-issued fallback frontier repair contract, the current nonterminal
inbound context/data session, and a chunk at the current frontier. Ordinary
completion alone does not satisfy those observation prerequisites.

Read-only AST verification also confirms one live scenario call, at
`GuiSmoke-Windows.ps1:42981`: `Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`.
It still omits the offline FrontierOwner handoff. The new owned-payload path
has not silently become a live path.

## Bounded next plan

1. **Preserve the completed negative control.** Keep canonical-fallback's
   completed-analysis/incomplete-route distinction, exact stop sentinel, 2/2/1
   parent checks, three evaluations/one native read and cleanup unchanged.
   Do not add another identical 256 MiB run to seek a different result.
2. **Design the eligible same-transfer producer episode before implementation.**
   Trace the existing coordinator-authorized frontier repair/admission path and
   its small native marker fixtures. Specify how a separate explicit offline
   profile exercises a real episode on the original held 256 MiB pair. The
   primary must independently select a genuine supported unresolved failure;
   if it does not, record unavailable and stop. Never seed coordinator state,
   append a made-up failure/marker, relabel this GUI failure, or borrow another
   transfer's receipt. An inability to obtain that episode is a contract/design
   blocker, not permission to broaden resolver eligibility.
3. **Implement only that reviewed test integration.** Carry its original
   producer completion, marker, diagnostic file and native writer custody
   through the already verified GUI/wrapper/parent/primary chain. Require the
   primary's original selection to resolve 1-64 exact native lines and bind to
   its original evaluation-owned transfer pair. Preserve all budgets, source
   identity, original evaluation count and default-off live behavior.
4. **Verify narrowly.** Start with affected small producer/resolver controls:
   missing marker/receipt, wrong transfer/writer, copied or retired custody,
   unsupported/no selected failure, and one-shot cleanup. Once stable, rebuild
   and authorize one offline integration run. A different first edge stops the
   slice; no automatic retries, extra evaluations or budget changes.
5. **Only then review live readiness.** Pre-verdict/failure accounting and live
   dispatch ownership still require their own verified contracts. Once ready,
   a fresh portable build and escalated strict preflights precede the targeted
   live-256 route-cycle run. Require Regular4 -> Tuna4 -> fallback6 -> Tuna4 ->
   fallback6. The full matrix is not the next gate.

This plan does not authorize a production fix or positive-fixture implementation
in this review. In particular, no changes to GUI scheduling, terminal-first
guards, coordinator ownership, retry cadence, queue/chunk policy, wallets,
performance thresholds or live acceptance are justified here.

## Verification and preservation

Inspection only; no tests rerun because no tested code changed. The GUI script
parses with zero errors, and the unique live-dispatch AST shape is unchanged.
Diagnostic observations and literal source counts are retained in
`artifacts/reactivation-window-review-20260928/REVIEW-EVIDENCE.json`.
The review adds only this document plus ignored evidence/seal files. Previous
source, binaries, reports and all 453 prior dirty entries remain unchanged.
The final pin and dirty-state audit is in the adjacent FINAL-VERIFICATION.json
and FINAL-CHECK.json.
