# Second-transfer harness retoggle: RCA and bounded fix plan

## Status and scope

RCA only; production and harness behavior are unchanged. Current baseline is
`8c2c6fbf874e36960cc0cd4251febf0491f471bc` on `v0.7.0`. The relevant live campaign
is `artifacts/initial-fallback-live-20260924-8c2c6fbf/live/20260924T043530Z`.
All 162 sealed live pins were verified before inspection. No live retry, build,
authority re-evaluation, commit, or push was performed for this investigation.

## Root cause

The harness's second-transfer preparation repeats an already completed **on**
action by calling `Unlock-TunaPayers` again. That helper is not an idempotent
ensure-on operation: if no wallet prompt is open, it unconditionally clicks
`SessionHeader.TunaUnlockToggle` (`tools/GuiSmoke-Windows.ps1:33582`).
The app intentionally treats a click while on as a request to switch off
(`src/nLink.App/Views/SessionHeaderView.axaml.cs:578`).

In the observed run, the reactivation step had already unlocked Tuna. After the
first transfer completed in fallback, the second-transfer branch at harness
lines 37897–37908 invoked the helper again. That switched Tuna off, then waited
for an unlock prompt that an off action does not open. Its exception was caught,
the readiness observation subsequently timed out, and the harness sent the
follow-up anyway while retaining the later coordinator-publication requirement.

This is a scenario-orchestration error, not evidence that the coordinator should
admit a conflicting route. The earliest attributable error to fix is the extra
header action. Do not change wallet behavior, route ownership, or receiver admission.

## Recorded sequence

Times below are UTC. W refers to the original
`second-transfer-after-reactivation/filetransfer-gui-log-writer-evidence-slice.log`;
M refers to `filetransfer-tuna-gui-milestone-evidence.log` in that directory.

| Time | Observation | Exact source |
| --- | --- | --- |
| 04:36:59 | Intended switch-off: `tuna_runtime_locked`, `header_switch_off` | W:27841, sender sequence 21191 |
| 04:37:02 | Harness prearms the requested on action | M:9 |
| 04:37:03 | Header unlock completes; step 2 records `on; prearmed=1` | W:30517, sender sequence 22950; M:13 |
| 04:37:25 | First transfer completed before same-transfer reactivation proof | M:14 |
| 04:37:29 | Another header switch-off occurs during follow-up preparation | W:43110, sender sequence 28762 |
| 04:38:52 | Follow-up offer sent over regular NKN v4 | W:44363, sender sequence 29396 |
| 04:38:52 | Receiver observes the offer, rejects `offer_route_not_active`, sends decline | W:44371, 44373, 44375; receiver sequences 14971, 14973, 14975 |
| 04:38:53 | Sender receives the decline | W:44386, sender sequence 29405 |

The receiver did receive the offer. The immediate reason there was no Accept
button was rejection before user acceptance, not demonstrated network loss or
an invisible enabled button. Source inspection shows this rejection can occur
at route resolution or initial-leg admission; the shared reason alone does not
identify which branch ran. That distinction is not required to correct the
earlier erroneous harness switch-off and must not be guessed.

The original parent recorded an outbound regular-v4 publication at retained-input
line 30246, boundary `fte1-6b87811041a6b8388ec8af2d`. Its route-consistency result
is PASS, with no selected failed coordinator boundary. Its route sequence remains
`file_tuna_v4 -> post_tuna_fallback_v6 -> regular_nkn_v4_fast`, not the required
Tuna/fallback/Tuna cycle. Acceptance remains FAIL / correctness / incomplete_proof.
The classification-enrichment timeout is secondary; increasing that timeout
would not correct the extra toggle.

## Deterministic reproduction

`artifacts/second-transfer-retoggle-20260924/Reproduce-Retoggle.ps1` imports the
current source's two unlock helpers and executes its exact second-transfer
fresh-readiness branch. Only the UI parameter type is changed to `object` so
synthetic controls can be passed. All GUI, log-wait, and wallet dependencies are
stubs; the click stub models the app's inspected on/off behavior. No real UI,
wallet, network, or authority evaluator is accessed.

Both Windows PowerShell 5.1 and PowerShell 7.6.6 reproduce:

`wallet_prompt -> runtime_unlocked -> header_switch_off`

The first call leaves the fixture on. The follow-up branch performs a second
click, leaves it off, submits no additional password, and catches the exact
wallet-dialog timeout observed in the live run. This establishes the harness
mechanism, not that a corrected live transfer will necessarily pass. An initial
AST selector in the reproduction script was narrowed to disambiguate inline
readiness conditions before either successful result was recorded.

## Fix contract

1. Remove the redundant `Unlock-TunaPayers` call and its catch block from the
   second-transfer fresh-readiness branch. Remove only the now-unused
   `$secondFreshUnlockError`, associated summary field, and obsolete message/assertions.
   Retain the diagnostic readiness observation and its explicit diagnostic-only
   status. Do not add retries, another off/on cycle, or new runtime controls.
2. Preserve the existing live on action: the prearmed path performs it once,
   while the non-prearmed on branch performs it once. Existing wallet-prompt
   servicing in those paths remains unchanged. Reaching the first-terminal
   branch is not a request to toggle again.
3. Preserve the new-transfer bookmark and required coordinator-authorized
   `file_tuna_v4` / protocol 4 publication after send/accept, including the
   zero-route-commit-generation requirement. Earlier readiness, a successful
   harness action, or the first transfer's publication cannot satisfy this gate.
4. Keep readiness-timeout handling, terminal/integrity checks, parent receipt
   ownership, and failure classification unchanged in this patch. A genuine
   later failure remains a failed attempt and becomes the next scoped RCA.

The smaller call-site correction is preferred to adding a general ensure-on API
or changing the button/UI accessibility contract. The extra call is unnecessary
on this path; broad idempotence machinery is not required to remove it.

## Verification and next gates

Add behavioral regressions using mock controls and AST-extracted production
statements. Check both PowerShell hosts and payer modes helpee/helper/both:

- After the intended on action completes, first-terminal/follow-up preparation
  issues no additional toggle click and preserves each payer's on state.
- Failure of the intended on action still propagates; follow-up preparation must
  not conceal it or claim an established route. The non-prearmed and prearmed
  paths retain their existing single-action and prompt-handling behavior.
- The fresh-readiness-false path introduces no new action.
- Missing second coordinator publication still fails, even with an old Tuna
  publication, listener readiness, a successful on action, or completed payload.

Update only affected source-contract assertions in
`FileTransferGuiHarnessScriptTests`: the preactivation-readiness test, separate
second-transfer-proof test, and existing live-reactivation prompt test as needed.
Run the new focused regressions first, then the directly affected harness/proof
tests once after stabilization. No Core/Infra production lane is needed unless
the eventual diff actually changes those paths. Do not run aggregate suites.

After review/checkpoint and a fresh portable rebuild, rerun only
`second-transfer-after-reactivation` outside the sandbox with strict preflights,
no implicit retry, and the same evidence-ownership rules. Require the second
Tuna-v4 publication plus successful terminal/integrity checks. The initial-fallback
startup fix still needs explicit live coverage: this campaign contained zero
initial-fallback binding/startup boundaries. Do not call it live-validated based
on route consistency or eventual completion alone. The full matrix remains gated.
