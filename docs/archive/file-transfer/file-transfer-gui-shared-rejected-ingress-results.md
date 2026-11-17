# Shared rejected-ingress matcher: bounded shadow implementation

Implemented from `5cb1c3e7` under the approved four-active-hour bound in
[the plan](file-transfer-gui-shared-rejected-ingress-plan.md). This report records
the scoped commit's validation. Primary GUI recovery and Snapshot arbitration are unchanged.

## Implementation

- Moved 22 existing functions into
  `tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1`. All moved function
  bodies match the committed source after line-ending normalization. The index
  keeps its original buckets, event order, matching rules and visit accounting.
- `ArtifactWriters.ps1` imports that module; it still owns the broad event catalog,
  outer mismatch reduction, route-commit/send-drain dependency and separate scalar.
  The shared module imports nothing and does not reset parser caches.
- Added the explicitly invoked, diagnostic-only
  `tools/FileTransferOps/GuiRejectedIngressShadow.ps1` bundle. It initializes the
  existing parser before analysis, parses input once, selects nine exact event
  types, builds one index and calls the shared matcher once per identical-record-
  deduplicated receive. All contributing records remain in the index, including
  conflicts. The primary GUI decision is returned unchanged by reference.
- Original `gui_evidence_sequence`, writer identity and writer sequence survive
  partitioning. The adapter reuses parser semantics, including uppercase event
  keys and repeated fields. Its diagnostic output is not a persisted schema or
  Coordinator authority.
- Added both modules to the existing script dependency/parser inventory. Ops/Soak
  scripts are repository tools, not Windows app-package payloads, so the portable
  payload manifest is unchanged. Frozen LegacyV0 source IDs remain historical
  metadata; catalog bytes/hashes are unchanged.
- The production GUI and wrappers neither import nor call the shadow adapter.
  Its parser-initializing bundle is for isolated test/diagnostic initialization,
  not dot-sourcing into an in-progress retained analysis.

## Intentional policy differences

These are captured before extraction and pinned on both PowerShell hosts. They
are not resolved by this refactor. “Shared” means the per-receive retained matcher,
not the primary GUI verdict or the entire retained report.

| Fixture | Primary GUI | Shared matcher |
|---|---|---|
| Exact initial activation | Recovered | Recovered |
| More than one qualifying source | Unresolved | Recovered |
| Source after the selected transition | Unresolved | Unresolved |
| Replaced route before receive | Unresolved | Recovered |
| A→B→A route history | Unresolved | Unresolved (ambiguous transition) |
| Terminal between receive and mismatch | Unresolved | Recovered |
| Terminal between mismatch and ignore | Unresolved | Unresolved |
| Complete second-cycle resend and receive | Unresolved | Recovered |
| Second-cycle Tuna receive missing | Unresolved | Unresolved, precise missing-receive variant |
| Tuna receipt before transition, or different receipt publication tuple | Unresolved | Recovered under existing receipt-window rules |

The retained rule proves its existing payload/range/bytes/count and transport
window, not a stronger exact-current-publication receipt contract.

Count domains also remain different. An identical duplicate receive yields GUI
`1/1/1/0` versus retained `2/1/1/0`, scalar `0`. Conflicting receive records yield
GUI `1/1/0/1` versus retained `2/2/1/1`, scalar `1`. These are explicit controls,
not a global quartet/scalar parity claim.

## Validation

Release smoke build: zero errors, 221 existing warnings. The new focused gate
passed 4/4 xUnit cases, including dual-host characterization/parity, dependency
isolation and authoritative early-return no-call checks.

The single final joined gate passed **17/17** in 128.099 seconds, with zero failed
or skipped cases. Details are recorded in
`artifacts/validation/consolidation-bounded-20260907/implementation-gui-ingress-shadow/RESULTS.md`.
It covers the existing 46-case GUI safety matrix, nine-case route/proof matrix,
22-case rejection diagnostic matrix, six index regressions, initial synthetic
full/compact control and generated transport/redaction/fresh-compact contract.

The new matrix pins 20 pre-extraction cases, compares independently parsed full,
regenerated compact and shadow inputs, and checks selected/unfiltered original
locations, empty/singleton input, exact duplicates, conflicts and parser controls.
JSON equality is per-receive diagnostic equality; each primary GUI decision and
the separate retained quartet/scalar are checked independently.

Independent review found no remaining actionable extraction or policy-drift
issue. The two unrelated writer null guards and frontier diagnostic files remain
untouched. Historical `20260910T060311Z` input SHA is unchanged. Its saved helper
9564 finding remains unresolved (`25/25/18/7`, scalar `14`); no historical report
was rerun and no missing Tuna receipt was reconstructed.

Conservative cumulative active work for this bounded phase is 107 minutes across
all agents, below the four-hour allowance. Build and test execution is recorded
separately. One existing focused test produced two small synthetic full/compact
reports; no standalone historical end-to-end report or campaign launch occurred.

## Cutover decision

Do not enable the adapter in live GUI classification yet. The next separately
approved design decision is how a non-authority data-path diagnostic accompanies
an authoritative Snapshot result, which candidate/count domain is presented,
and whether stricter GUI safety/receipt-scope rules become shared policy. It must
not override Snapshot evidence failures or Coordinator authority boundaries.

No runtime, DTO, protocol, Snapshot contract/profile/catalog, timeout, route policy,
portable package, preflight, live run or historical finalization change is part of
this implementation. Campaign 6/6 and standalone historical reports 3 remain
exhausted; this phase does not restart either allowance.
