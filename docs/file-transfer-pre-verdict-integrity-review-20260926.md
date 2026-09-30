# Pre-verdict integrity integration review

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-terminal-payload-join-20260925.md`.
Review only: no code or acceptance change.

## Decision

The next bounded implementation is a **default-off, test-only pre-verdict
integrity shadow**. Consume the existing owned terminal/payload join before
the original verdict block, and observe the independent gate factors in that
same invocation. Leave the production GUI integrity, verdict, reports, parent
acceptance and alternative-acceptance contract untouched.

Do not implement a production cutover yet. The current join runs in cleanup,
after reports; the primary also collapses absent, unreadable and false GUI
integrity into one Boolean. Neither a late diagnostic Boolean nor a blanket
OR into `guiIntegrityOk` is a safe replacement.

Success for the next milestone is an owned integrity decision available at
the correct time, with conflict/expiry controls and unchanged original gate
results. It is not an overall PASS, a new route proof, or live-256 readiness.

## Verified current state

All 11,007 pins in
`artifacts/terminal-payload-join-20260925/FINAL-VERIFICATION.json`
match. The preceding seal SHA-256 is
`4801e6178ff4078e9bb2d56229e99db16fd1c8003bc26b4cbbfef61acf76b121`.

The two final positive fixtures were reread, not rerun:

- `artifacts/terminal-source-tokens-20260925/30b6bf15-a85f-44fb-a76f-e5fa76892581/primary/`
- `artifacts/terminal-source-tokens-20260925/fab8d16c-11a3-4219-9717-f2f0969e03c6/primary/`

Both have no GUI summary, completion 1, integrity 0, no hard failures,
no liveness timeouts, no selected first proof-chain failure, passing snapshot
artifact/parity checks, and original `FAIL_PROTOCOL_OR_INTEGRITY`.
Their route summaries show only Regular-v4 and a failed live-route-epoch
proof. Thus even `first_proof_chain_failure_class=(none)` does not mean that
all independent gates pass. The valid offline payload join does not erase
the known failed route-cycle requirement.

The previous 25 distinct passing tests remain inherited evidence. No new test
passes are claimed here.

## Exact insertion point and lifetime

References below are current lines in
`tools/FileTransferOps/AnalyzerOrchestration.ps1`.

1. The original primary pair is taken at 2875-2876, after the original full
   and compact evaluations. Original arrays remain inside the private context
   after the local array variables are cleared at 2881-2883.
2. GUI input is read once at 3057; completion and integrity are converted at
   3058-3059. Terminal inference at 3115 changes completion only.
3. Proof candidates are complete by 6124. Selection at 6125-6142 filters
   authoritative families and chooses the chronological first candidate.
   Warning inputs are ready by 6174.
4. Insert the new optional shadow immediately before the unique three-line
   block beginning `$verdict = 'PASS'` at 6176. This is after the required
   original evidence is available, but before any acceptance classification.
5. Leave the entire original classification block at 6176-6366 intact.
   Decision-bearing reports begin at 7168, terminal SHA output at 7679,
   compact JSON at 9167. Stage diagnostics can be written earlier; do not
   claim that no artifact exists before this seam.
6. Cleanup at 9227-9245 remains authoritative for lifetime. The current
   `PublicationLivePrimaryObserver.ps1` inserts the terminal probe immediately
   before clearing `localPublicationWitness/localCompletedEvaluations`.
   That is after original report construction and cannot be the new
   consumption point.

Use the existing test observer's exact, unique seam checks. Do not edit or
duplicate the production analyzer function on disk. A missing/ambiguous seam
must fail the test instrumentation, not silently fall back to late consumption.

The earlier source/payload probe needs two responsibilities separated:
perform membership/origin/payload work and owned consumption at the pre-verdict
seam; emit detached diagnostic observations at the existing post-report seam.
Do not rerun the one-shot membership or join in cleanup. Original verdict
values do not exist at pre-verdict time; attach them only to output later.
Do not read that output back as a capability.

Reuse the exact worker, native files, reader, primary custody/pair, context,
arrays, views and cumulative work clock. A small guarded consumer beside
`TerminalPayloadJoin.cs` may use the membership's private state; it must not
accept `Joined=true`, a copied result, a JSON receipt, or the historical
`TerminalPayloadJoinWasIssuedForTest` bit as input. Revalidate the original
ownership and bounds at consumption, which is separately one-shot.
Cleanup/expiry/failed joins cannot issue a positive shadow result.

## GUI integrity input contract for the shadow

Keep the production reader and its compatibility conversions unchanged.
The shadow must observe the outcome of the existing read, not reopen the
summary or infer absence from `guiIntegrityOk=false`.

`Read-FileTransferCompactGuiSummary` (2115-2128) returns null both when the
file is absent and when reading/parsing fails. Its original absence and catch
branches therefore need test-only observation if absence is to be positively
distinguished. Preserve its return value and exception behavior.
Retain the raw property presence, nullness and Boolean type before the
conversion at 3059; do not treat a nonempty string such as `"false"` as a
valid Boolean success.

| Observed GUI input | Current owned join at consumption | Shadow integrity result |
| --- | --- | --- |
| Positively observed file absence | Valid | Owned integrity proven for this fixture only |
| Parsed Boolean true | Valid | Owned integrity proven; GUI input agrees |
| Parsed Boolean false | Valid | Conflict; no replacement or promotion |
| Parsed Boolean false | Invalid/unavailable | Explicit failure remains |
| Unreadable/malformed input, missing/null/wrong-type property, or unknown read outcome | Any | Unproven/invalid input; no promotion |
| Absent input or Boolean true | Invalid/unavailable | Owned integrity unproven; GUI true is not ownership |

These are proposed shadow states, not changes to original acceptance.
A rejected join is not automatically a measured hash mismatch. Keep
unavailable/expired evidence distinct from an affirmative conflicting input.
Do not manufacture a GUI summary for the genuine positive fixture.

## Independent compact-gate inventory

The complete decision structure in the compact primary is 6176-6366.
Capture all of the following original inputs, including zero, false, none
and non-applicable values. Keep identity/reference associations private and
emit only detached diagnostics. This inventory is not a new route authority
or an exported acceptance token.

| Gate layer | Original factors and behavior to preserve |
| --- | --- |
| Completion | `guiCompleted`; false produces setup/inconclusive (6179-6183) |
| Base correctness | `guiIntegrityOk`, hard-failure count, active Tuna-v6 count, route consistency verdict, live-route proof verdict (6184-6192) |
| Liveness | Pre-terminal liveness timeout count, independent of completion/hash (6193-6197) |
| Warning policy | Original cap object and exceeded kinds; warning kinds/count; preserve fail versus warn (6198-6207) |
| Incomplete-proof override | All five property/count predicates at 6209-6213 |
| Route/runtime override | All 37 property/count predicates at 6218-6254, including closure/adoption/liveness and handoff-bypass subcounts, not just top-level unresolved counts |
| Other candidates | Original list and array, count and every candidate; nonempty list can fail independently (6259-6263) |
| First-edge classification | Exact selected object, class, reason, family-selection flags and chronological ordering; preserve the special reason and every class branch at 6269-6357 |
| Artifact/parity override | Snapshot mode plus artifact verdict, and full/compact parity verdict; this final override stays intact (6360-6366) |

The five incomplete-proof properties are:

- `runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount`
- `regularV4FrontierRepairSettlementDispositionProof.UnresolvedCount`
- `regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCount`
- `runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCount`
- `runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCount`

For the 37 route/runtime properties, the pinned source range 6218-6254 is
the exact inventory: one named property per line. A coverage assertion should
compare the shadow's named factors with those actual predicate inputs so a
future added predicate cannot silently disappear. Do not reconstruct the
inventory from report fields that happen to be nonzero.

Retain the unfiltered `compactProofChainCandidateArray`, its original list,
and selected-first reference separately. For data-path mismatch, also retain
the original `CandidateRecords` and `EffectiveCandidateRecords`, with existing
recovered flags/diagnostics: the compact list is not a complete history of
recovered candidates. Never subtract one admitted mismatch from an aggregate
count and conclude that all other gates pass. No recovery, proof matcher or
authority evaluation is to be rerun to populate this observation.

Do not produce `would_pass`, `CanonicalScenarioProven` or alternative
acceptance from this inventory. Parent scope and other same-attempt evidence
are still outside the bounded terminal fixture. A missing factor or exceeded
observation budget is an incomplete diagnostic, never an implicit pass.
Preserve the original distinction between not applicable, unavailable and
evaluated/pass; zero candidate events alone do not prove an applicable route.

## Outer acceptance remains independent

The compact analyzer is not the whole scenario acceptance path.
`tools/Run-FileTransferRouteAcceptance.ps1` additionally consumes:

- Route presence, selected sequence and protocol/runtime metadata in
  `Assert-Phase4RouteSummary` (3498-3645), including the existing explicit
  prefix/suffix policies. This review neither widens nor removes them.
- Operator hard failures, warning caps and warning policy at 4182-4207.
- The selected measured/second-transfer GUI phase, its completion/integrity,
  route/protocol and both terminal states/errors at 4776-4827.
- The actual scenario definition at 9500/9514: 268,435,456 bytes, helpee payer,
  switch-off fault, on/off/on/off toggles and
  Regular4/Tuna4/fallback6/Tuna4/fallback6.

The 6671-6759 GUI/route checks previously cited are specifically the
**retained-analysis timeout diagnostic path**, not the normal scenario
acceptance implementation. It remains inconclusive at 6760-6769 even when
terminal success is observed. Do not promote an analysis timeout based on
file integrity.

The historical scenario-handoff review also described terminal inference as
setting integrity. Current 3115-3117 does not do that; the earlier wording is
superseded. Historical reports and pins are preserved rather than rewritten.

This review maps the compact verdict completely and identifies the outer
boundaries; it is not a claim to have revalidated every parent/matrix policy.
No parent acceptance function is changed by the proposed shadow milestone.
Goodput/performance remains separate from correctness.

## Bounded implementation and checks

Suggested scope: the existing test-only primary observer and terminal probe,
the guarded terminal join consumer, and one focused test partial. Reuse
existing fixtures and native handoff. No production runtime/coordinator,
packet version, payload limit, GUI producer or acceptance-runner edit.

1. Observe the original GUI read and raw integrity type/status. Install the
   pre-verdict hook only under a new explicit test flag; default-off behavior
   remains identical.
2. Relocate the genuine membership/origin/payload work for that mode, consume
   its current owned result once, and capture the named original gate inputs.
   Use the existing original clock; do not reset it or include a second
   writer read/hash. No generic acceptance framework is needed.
3. Emit the shadow and original outcome after original reports, then verify
   retirement in existing cleanup. Do not mutate original arrays, views,
   candidate objects, counters, failure selection or reports.

Keep the existing 256 KiB payload, 4 MiB log, 16 KiB packet, 4,096 cumulative
record visits, 20-second optional work, code-unit and boundary/witness limits.
Charge any new candidate traversal/string inspection to the existing
cumulative accounting. Preflight bounded collection sizes and reject the
optional observation if it would exceed them. No unbounded report dump or
whole-log replay disguised as an inventory.

Focused verification, both PowerShell hosts for genuine positives:

- Owned integrity is consumed before verdict/report construction; three
  original authority evaluations remain three; no extra writer read or
  primary payload hash; original decision-bearing outputs remain unchanged.
  Compare stable decision fields, not timestamps or elapsed timings.
- Absence, Boolean true/false, malformed/unreadable, missing/null and
  wrong-type GUI inputs obey the table. Deliberately altered GUI inputs are
  predicate controls, not real transfer failures.
- Valid integrity with the existing failed live-route proof still reports
  that failure. Data-driven controls retain each independent predicate and
  combinations where an earlier failure masks a later one, plus recovered
  and multiple mismatch candidates. Label in-memory controls accurately.
- Copied/foreign/expired/disposed ownership, second consumption, post-join
  expiry and exhausted new-work budgets cannot yield positive consumption.
  Cleanup retires the usable result; historical output cannot restore it.
- Disabled instrumentation and early failure/cleanup paths preserve the
  original outcome. Changed/multiple hook seams fail explicitly.

Run only the new focused cases, then directly affected payload-join,
membership and observer controls once after stabilization. Unchanged native
packet lanes and aggregate suites do not need another run solely for moving
the consumer. No portable rebuild or live test is part of this milestone.

After that succeeds, the next boundary is still production/same-attempt
integration: equivalent real GUI producer custody, declared parent scope/size,
all required route legs and independent evidence, and separately approved
acceptance adoption. Do not combine this Regular-only fixture with a different
Tuna fixture to claim one passing canonical attempt.

## Verification and preservation

All 11,007 inherited pins and all 163 baseline dirty files were verified
unchanged at completion. This document is the only new non-ignored file.
`git diff --check` and the document whitespace check pass. The unique
pre-verdict seam and the five/37 predicate inventories were checked directly
against current source.

The review seal is
`artifacts/pre-verdict-integrity-review-20260926/FINAL-VERIFICATION.json`.
No code edits, test runs, build, portable packaging, network/GUI run, full
matrix, commit or push were performed. Acceptance remains unchanged.

