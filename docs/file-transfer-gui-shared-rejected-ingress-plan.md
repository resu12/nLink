# Bounded next phase: shared rejected-ingress evidence and GUI shadow parity

Implementation approved and completed on 2026-09-10. See
[the results and deferred cutover decision](file-transfer-gui-shared-rejected-ingress-results.md).
The original plan below records the authorized scope, not a live-cutover approval.

## Recommendation

Start from committed `5cb1c3e74c02d6fdccd6a95e853c38f3da3aa670`.
Extract the existing rejected-ingress matcher once, then prove that a thin GUI
diagnostic adapter produces the same per-receive result on independently parsed
evidence. **Keep GUI recovery selection unchanged in this phase.**

This refines the earlier second-activation GUI recommendation: source review
found real contract differences, so a direct replacement is not a safe wiring
change. Avoid building another second-cycle matcher inside the legacy GUI.
The shared diagnostic adapter is preparatory work for a separately approved
cutover, not a claim that authoritative GUI integration is complete.

Proposed bound: at most four cumulative active engineering hours across agents,
one final focused joined gate after stabilization, no aggregate suites,
standalone historical reports, portable builds, preflights, or live attempts.
Stop and report if extraction requires broader reporting/authority redesign.
Implementation of this plan requires separate approval.

## Evidence and constraints

The committed fix passes the two formerly failing gates. Its final joined
selection passed4/4, with46 GUI and9 route/proof cases verified onPS5.1/PS7.
The generated transport/redaction/fresh-compact contract also remains green.
Those tests were run on the reviewed worktree with unrelated edits preserved;
this is not a claim of a fresh clean-checkout package or live acceptance.

Historical `20260910T060311Z` remains failed at helper9564. Saved quartet
25/25/18/7 and scalar14 remain unchanged. Its exact matching Tuna receive is
absent; neither envelope identity, explicit rejection, later fallback delivery,
nor terminal SHA supplies that receipt. No historical evidence is reconstructed.

The exhausted campaign remains6/6 and standalone reports remain3. No new
report/campaign allowance, runtime timeout, or acceptance exception follows from
this plan. Preserve the unrelated frontier diagnostic files and two
`FallbackFrontierStallDiagnostic` null guards still in the worktree.

## Why direct GUI cutover is deferred

1. **Different safety checks.** The GUI reducer requires a unique opposite-writer
   source, source-before-transition order, current first-activation eligibility,
   contributing-record conflict checks, and terminal/replacement/ABA revocation.
   The retained matcher iterates earlier sources and historical exact transition
   candidates. These behaviors are not interchangeable.
2. **The existing Tuna receipt rule is narrower than a full current-publication
   receipt contract.** It requires matching payload identity/range/bytes/count,
   Tuna transport, protocol and direction in the window between the original
   Regular send and rejected receive. It does not universally require the Tuna
   receipt to follow the receiver transition or equate every receipt publication
   generation with that transition. Preserve this rule during extraction; do
   not silently tighten it or describe it as stronger provenance than it proves.
3. **Different count domains.** GUI candidates are receives. Retained analysis
   also accounts for sends/unattached mismatch events, corroborating route-drain
   evidence and a separate scalar. Per-receive diagnostic parity is not global
   quartet/scalar/arbitration parity.
4. **Authoritative GUI returns earlier.** SnapshotV1Authoritative classification
   returns before the legacy data-path reducer, including on an authority-pass
   result. A legacy adapter alone cannot expose the historical operator finding
   through that production path. Do not move a legacy rule ahead of Snapshot
   validation or let it infer/override Coordinator authority.

## Implementation order

### 1. Freeze the existing contract with characterization fixtures

Use existing generated fixtures; no new historical report is needed.
Capture the shared result fields (`Recovered`, `RejectionChainMatched`,
`ReceiveDisposition`, `Reason`, `Variant`) and candidate location before moving
code. Keep full/compact quartet and scalar results as independent controls.

Add explicit characterization for known distinctions: more than one source,
source after transition, replaced/ABA transition, terminal inside an open chain,
and Tuna evidence before the transition or from a different publication tuple.
Record the current outcomes separately for GUI and retained matching. A
disagreement is a policy/design input, not permission to choose the convenient
verdict. Do not weaken existing GUI negatives to force parity.

### 2. Extract one dependency-bounded matcher module

Proposed file:
`tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1`.

- Move, rather than duplicate, the exact matcher/Boolean wrapper, rejected-chain
  and companion checks, range/identity helpers and indexed lookups needed by
  `Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult`.
- Move the existing correlation index with unchanged bucket contents, ordering,
  matching and visit statistics. Build it once per input; no new per-candidate
  whole-stream scans or performance-tuning project.
- Keep existing function signatures and Boolean behavior for retained callers.
  Keep the outer `Get-FileTransferFileTunaV4DataPathMismatchProof`, its separate
  scalar/candidate reduction and route-commit/send-drain correlation outside the
  small module. Do not import `ArtifactWriters.ps1` or `AnalyzerOrchestration.ps1`
  into GUI merely to access this matcher.
- Reuse `LogParsing.ps1` at isolated shadow/test-bundle initialization, before
  any retained index exists. Its only transitive import is the small authority-
  matching module; loading defines functions and resets retained index/query
  caches. Preserve retained callers' existing import order. The shared core must
  not unconditionally reimport/reset the parser mid-analysis. Do not move widely
  used parser functions or duplicate field-access semantics. Stop if another
  broad extraction is required.
- Keep the retained broad event catalog with its existing owner. Define/test the
  adapter's exact required event subset from the moved index/chain requirements;
  retain original lines, writer locations and `gui_evidence_sequence`.
- Verify script packaging/dependency manifests include the new module, without
  rebuilding a package. Add structural tests for a single implementation and no
  accidental broad analyzer import.

No change to public APIs, DTOs, runtime policy, Snapshot contract/catalog/profile,
report schema, authority routing or deadlines.

### 3. Add an opt-in diagnostic shadow adapter

Add a small test-invocable GUI adapter that parses its selected evidence once,
builds one shared index and calls the shared result for each canonical receive.
It must not create a second implementation of the Tuna resend/receive predicates.

Keep separate fields for the shared diagnostic and the existing primary GUI
decision. A shared recovered result must not bypass the current GUI source,
conflict, terminal, replacement or ABA vetoes. Keep the new shadow path out of
ordinary live classification until a later cutover decision; no new background
evaluation cost or timeout change is included here.

For compatible fixture subsets, require byte-identical normalized shared results
from retained-full, regenerated-compact and GUI-adapter inputs. Normalize original
candidate identity/location and existing diagnostic fields, not fresh synthetic
writer sequences or a new persisted schema.

For a complete second-cycle fixture, the shared result may be recovered while
the primary legacy GUI remains conservatively unresolved. Label that difference
as expected/deferred, not as zero policy differences or recovered GUI authority.
Missing Tuna receive remains unresolved in every path. Retained/shared shadow
carry the existing precise variant; the unchanged primary GUI keeps its generic
mismatch classification. Keep authoritative GUI early-return behavior unchanged
and test that the shadow helper is not invoked by that path.

## Focused verification and exit

1. Parse changed PowerShell onPS5.1/PS7; Release-build only the affected smoke
   project if its tests changed.
2. Run extraction-equivalence and per-receive adapter tests on both hosts.
   Reset parser/index caches between independently parsed fixture cases.
3. Preserve the existing46-case GUI safety matrix,9-case route/proof matrix,
   22-case rejection-diagnostic matrix and separate generated transport/redaction/
   fresh-compact contract, rerunning only directly affected tests.
4. Cover initial and second cycles; missing send/receive/transition/companion;
   writer/session/transfer/range/bytes/scope mismatch; duplicate/conflicting
   contributing evidence; terminal/replacement/ABA; empty/singleton input;
   original locations and unrelated telemetry. Keep arbitrary conflicting
   invalidator identities outside the current guarantee unless separately scoped.
5. Run one small joined gate after stabilization. Require unchanged retained
   results, unchanged primary GUI decisions, and exact shared diagnostic parity
   on independently selected inputs. No full historical or production-width
   report is part of this gate.

Exit deliverables: one shared implementation, dependency tests, passing focused
equivalence, an explicit table of intentional GUI/retained differences, and a
short cutover decision. No source/receipt reconstruction or global parity claim.

## Separate decision after this phase

Choose the intended authoritative-GUI diagnostic contract before enabling it:
how a non-authority data-path failure accompanies a valid Snapshot boundary,
which existing candidate/count domain it uses, and how it is reported without
overriding Snapshot evidence-invalid or Coordinator-authority failures.
Decide whether any legacy safety checks or receipt-scope requirements should
become shared policy; do not silently settle those choices through refactoring.

Only after that decision and focused proof gates should a new bounded
package/preflight/live allowance be proposed. Another live run is not the next
action in this plan.

## Source anchors at the planning revision

- [Current exact retained result](<C:/Users/Juraj/Desktop/Remote help/tools/FileTransferSoak/ArtifactWriters.ps1:9850>)
- [Existing Tuna send/receive window](<C:/Users/Juraj/Desktop/Remote help/tools/FileTransferSoak/ArtifactWriters.ps1:10053>)
- [Index construction](<C:/Users/Juraj/Desktop/Remote help/tools/FileTransferSoak/ArtifactWriters.ps1:9336>)
- [Outer retained reduction/dependency](<C:/Users/Juraj/Desktop/Remote help/tools/FileTransferSoak/ArtifactWriters.ps1:15509>)
- [Authoritative GUI early return](<C:/Users/Juraj/Desktop/Remote help/tools/GuiSmoke-Windows.ps1:7217>)
- [Primary legacy GUI reducer](<C:/Users/Juraj/Desktop/Remote help/tools/GuiSmoke-Windows.ps1:7301>)
- [Verified current fixes](<C:/Users/Juraj/Desktop/Remote help/artifacts/validation/consolidation-bounded-20260907/implementation-rejected-ingress-follow-up/RESULTS.md>)

Prepared from source and saved test results only. No new build, test, evaluator,
report, package or live execution was performed while preparing this plan.
