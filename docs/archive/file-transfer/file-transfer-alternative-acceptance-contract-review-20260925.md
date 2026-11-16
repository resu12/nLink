# Alternative acceptance contract: review only

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the existing uncommitted work.

The user approved reviewing an alternative contract, explicitly keeping code
and acceptance unchanged until separately approved. This document is a proposal,
not implementation approval, a new verdict, or permission to rerun live tests.

## Recommendation and policy tradeoff

Consider a separately versioned, observation-based contract that evaluates three
obligations independently: recorded stale-packet rejection, actual use of each
required route, and transfer completion/integrity. Do not redefine `Recovered`
or silently weaken the current exact-Tuna matcher.

The intentional relaxation would be this: a rejected old Regular packet would
not need an identical-range Tuna replacement **before its rejection**. Required
Tuna payload evidence would instead belong to the correct route publication,
independently of that rejected packet. This is a different acceptance claim,
not a correction proving the existing live failure was a false positive.

| Alternative | Assessment |
| --- | --- |
| Keep the strict rule | Valid if the product requirement really is exact replacement before rejection; no change needed. |
| Allow the same-range replacement later in the same Tuna publication | Smaller temporal change, but still requires a per-rejected-range Tuna replay that rejection does not authorize or schedule. It does not cover legitimate later fallback delivery. |
| Separate recorded rejection, route use and completion | Recommended for a bounded shadow evaluation if separately approved; removes the replay dependency and makes the weaker observational claim explicit. |
| Ignore all rejected packets when SHA passes | Reject: loses source, authority, route-use and contradiction checks. |

Do not revive `proven_stale_rejected` from the older proposal by renaming its
missing proofs. If acceptance must certify execution-time security validity and
absence of every stale-callback effect, this alternative is insufficient; retain
the strict policy while separately specifying the missing production proof.

## Current evidence and its limits

The current second-Tuna matcher binds an earlier Regular source and the exact
receiver publication, then requires same-range Tuna send/receive in its retained
pre-rejection window. `ArtifactWriters.ps1` removes records from effective
mismatches only when recovery is established. A recorded rejection alone cannot
make that Boolean true.

The original-custody diagnostic chain now checks forward/reciprocal yield
receipts, the rejection chain, a bounded dispatch-observation window and recorded
security-context spans. These remain test-only, default-off observations.
`no_matching_bound_dispatch_observed_in_capture` is not proof of no effects;
`matched_recorded_security_context_spans` is not execution-time key validity.
`SecurityLifetimeProven` and `RuntimeRetirementProven` remain false.

The seven prepared-callback tests assert specific unchanged state after the
successor publication. They do not cover preparation effects, active proof
completion, real-adapter recovery, scheduler/reset races, all callback families,
or arbitrary live execution. The shared `rejected_before_dispatch` label also
does not distinguish ingress rejection from a previously prepared callback.
Neither these tests nor an absence of dispatch logs supplies a general live
stale-safety certificate.

The earlier Service fixture proves a controlled later fallback disk write. Its
reopened-range assertion is not a production durable-write receipt. Whole-file
SHA would prove final file equality, not which packet caused an individual write.

## Proposed scope and required observations

Limit initial evaluation to the canonical
`regular-v4-live-activation-off-on-off-256mb` scenario and its second-Tuna rejected
Regular-v4 payload candidates. First activation, control frames, other failure
families and other scenarios retain their existing treatment.

For each candidate whose original strict failure is `missing_tuna_resend` or
`missing_exact_tuna_receive`, require all of the following:

1. Complete, immutable original inputs and passing original authority views;
   original candidate membership, writer custody and source locations. A detached
   JSON result, a log fragment, a cache or a copied diagnostic status is not an
   authority capability. Full and compact views must agree.
2. One exact old authenticated Regular payload source and its matching rejected
   receive: session/transfer, establishment/security identity, authenticated
   payload reference, range, bytes and count. Outer message ID or equal local
   generation numbers alone cannot establish this correspondence.
3. Validated original Coordinator-issued source/receiver publication histories
   and reciprocal transition receipts connecting the old source to the selected
   second Tuna publication. Never compare independent peers' generation numbers
   as shared IDs or use merged log order/wall-clock timestamps as a causal clock.
4. The exact receiver rejection chain under that publication, with an explicit
   rejected-before-dispatch disposition. Missing/ambiguous companions, conflicting
   candidate-bound dispatch or contradictory authority/effect observations make
   the candidate unproven, not exempt. Unrelated traffic is not a contradiction.
5. Consistent recorded security installation/use/retirement spans and bounded
   observation coverage for the selected identities. This checks recorded
   consistency only; it must not emit execution-time lifetime or retirement proof.

The proposed result name is `recorded_stale_route_rejection`, explicitly distinct
from recovery and general safety. A no-match dispatch query is supporting
negative evidence only; it cannot qualify a candidate without the positive
source, authority and rejection chain above. Even a qualifying result does not
certify unlogged effects. These are proposed admission requirements, not a claim
that the current diagnostic exports already form a production acceptance API.

## Independent scenario gates: no waiver

Acceptance under any future policy would still require all of these, together:

- The full Coordinator-published route cycle: Regular-v4, Tuna-v4, fallback-v6,
  second Tuna-v4, final fallback-v6, with the existing route/epoch, ownership,
  runtime-unlock, fallback, liveness and control-plane gates. Terminal completion
  before second Tuna activation does not satisfy the cycle. Active Tuna-v6 is
  invalid; readiness, ACKs and caches cannot substitute for route publication.
- Positive payload use on **each** required Tuna activation: an exact successful
  send/receive pair bound to the actual payload and corresponding publication on
  both peers. Match range/bytes/count and authenticated payload identity, not
  just throughput, queued bytes or a route label. A first-activation pair cannot
  stand in for the second activation. Keep all independent scenario payload
  obligations; only the rejected-range replacement dependency described above
  changes. This minimum cannot reduce another required payload set to one pair.
- Each payload observation must follow its own local publication and precede
  its supersession/terminal boundary. The valid pair may precede or follow the
  rejected packet. Its range need not equal the rejected range: that independence
  is an explicit policy relaxation, not an exact-range recovery claim.
- Successful terminal transfer, the expected file size and existing whole-file
  integrity/SHA checks. Fallback delivery can contribute to completion but cannot
  replace Tuna payload evidence. Goodput remains separate from correctness;
  no throughput threshold, chunk size, retry or queue policy changes are proposed.
- No unresolved failure outside the narrowly eligible class, and complete
  adjudication of every eligible original distinct candidate. Truncation, budget
  exhaustion, unknown schema, restart ambiguity or missing evidence is unproven.
  A passing subset must not hide an unchecked candidate or cancel another failure.

The current aggregate route/data-path verdict is not, by itself, proof of this
new per-publication payload requirement. A shadow implementation must verify
whether the already-owned evidence supports it; missing bindings stay unproven.

## Proposed decision controls

These are specification examples, not passing tests or historical verdicts.

| Evidence | Proposed treatment |
| --- | --- |
| Existing strict exact recovery plus every scenario gate | Preserve the strict result; no alternative exemption needed. |
| Fully bound recorded stale rejection; independent exact payload on both Tuna activations; all other gates pass | Eligible for a separately reported alternative pass, even if the original strict result fails. |
| Only later same-range fallback delivery and final SHA | Insufficient without independent Tuna route/payload proof and the complete rejection chain. |
| Different range substituted for one half of a Tuna send/receive pair | Unproven: pair mismatch. A different, internally exact pair in the correct activation is a separate route-use observation, not replacement recovery. |
| No second Coordinator-published Tuna, or no exact payload pair in it | Fail the independent scenario gate. |
| Bad/ambiguous source, security span, publication, rejection, or candidate-bound dispatch contradiction | No alternative exemption. |
| Complete matches for only some candidates; missing/truncated/over-budget remainder | Incomplete; no alternative pass. |

## Approval and implementation boundary

If this tradeoff is approved, the recommended first implementation is a bounded
**test-only shadow** of the proposed decisions, not an acceptance cutover. Reuse
the already owned completed views and original primary-selected witnesses;
preserve the existing three evaluations and all work/witness limits. No raw
reread, extra authority evaluation, new runtime producer, wire field, retry,
durable-write correlator or expanded capture is implicit permission.

Keep legacy reason/variant, `Recovered`, counts, primary verdict and persisted
reports unchanged. Use a separate versioned shadow result with per-candidate
membership and explicit independent-gate outcomes. A missing/failed shadow must
not change the primary. Detached controls test predicate logic, not generated
authority; genuine offline integration must also exercise the positive bindings.
If existing custody cannot support a requirement, report that gap before
expanding scope. Acceptance adoption requires a later explicit decision.

No new live run is needed for this review. Any eventual live validation requires
the separately approved implementation, focused regressions, a rebuilt portable
package, successful escalated preflights and the single targeted current scenario.
Do not launch a full matrix or rerun unchanged predicates hoping for favorable
packet order. The sealed historical FAIL and its original 62/43/19
distinct/recovered/unresolved accounting remain historical facts, not results
recomputed under this proposal.

## Verification of this review

The unchanged focused test
`PublicationTunaPayloadIntegration_PrimaryRequiresExactSendAndReceive` passed
all three cases on the current compiled test assembly. Each case ran its genuine
two-process offline fixture through PowerShell 5.1 and 7, with three original
evaluations per host:

- `exact`: `recovered`, `Recovered=true`.
- `missing_receive`: `missing_exact_tuna_receive`, `Recovered=false`.
- `wrong_range`: `missing_tuna_resend`, `Recovered=false`.

Receipt: `artifacts/yield-acceptance-gate-review-20260925/results/strict-payload-gate.trx`.
This verifies the strict boundary; it does not test or approve the alternative.
All 73 pre-existing dirty-file contents and the prior callback assertion seal's
15 pins remain unchanged. No code, classifier, acceptance, portable package,
historical artifact, live run, commit or push changes in this review.

Source anchors: `tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1`
(second-Tuna candidate selection), `tools/FileTransferSoak/ArtifactWriters.ps1`
(`Get-FileTransferFileTunaV4DataPathMismatchProof`), and
`tools/Run-FileTransferRouteAcceptance.ps1` (canonical route cycle and second-Tuna
gate). Related claim limits are recorded in
`file-transfer-security-lifecycle-consumer-20260925.md` and
`file-transfer-callback-effect-assertions-20260925.md`.
Hashes and preservation evidence are in
`artifacts/yield-acceptance-gate-review-20260925/FINAL-VERIFICATION.json`.
