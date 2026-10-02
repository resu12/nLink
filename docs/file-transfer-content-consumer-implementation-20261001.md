# Bounded content consumer implementation

The reviewed content-coverage query is implemented as a default-off test-only
shadow. It reports a separate diagnostic, not recovery or acceptance. Production
runtime and analysis tools remain unchanged. This follows the
[consumer contract review](file-transfer-content-consumer-contract-review-20261001.md).

## Implementation

The [pure matcher](../tests/nLink.SmokeTests.Core/Support/PublicationContentCoverageMatcher.ps1)
binds the primary's selected rejected Regular-V4 chunk to its original successful
send and rejection companions. It requires exactly one containing second-Tuna
send, the same encrypted payload on an authenticated dispatched receive, each
endpoint's own coordinator publication/forwarding evidence and local observation
order. It rejects superseded publication uses, including the old Regular send.

Both sender records must carry complete canonical `sender_chunk_hmac_v1`
evidence in the same writer, transport, security installation and diagnostic
scope. All segment indices, lengths, counts, commitments and total bytes are
validated before the selected chunk commitment is compared. Missing, malformed,
retired, unequal, ambiguous, late or over-budget evidence remains unproven and
does not retain a successful coverage record.

The [primary probe](../tests/nLink.SmokeTests.Core/Support/PublicationContentCoverageProbe.ps1)
runs only under the explicit test flag. It reuses the original primary's private
custody, completed full/compact views, native inputs, writer endpoints and work
clock. It never reopens the source or reevaluates authority. Publication origins
and mapped witness sets must agree across views, and prior inputs/results are
checked again before the detached diagnostic is exported. The existing ceilings
remain 4,096 fixture rows, 256 boundaries per view, 64 operations per boundary,
1–64 supporting witnesses and the cumulative 20-second optional-work budget.
New query inspections also reserve against the existing producer budgets.

The security-span helper has an explicit four-observation content mode for the
old send, Tuna send, Tuna receive and rejected receive. Its existing
eight-observation packet/yield/rejection mode and callers are preserved. Neither
mode claims runtime key destruction or grants route authority.

The genuine two-process fixture now also sends a three-chunk batch with the
held chunk in its middle slot. The receiver privately compares the decrypted
held-wire bytes with the actual data-session receive. Existing first-slot
same/changed fixtures keep their original behavior and strict failure.

## Verification

The final focused lane passed three distinct cases on both PowerShell 5.1 and 7:
same bytes at slot zero, changed bytes, and same bytes at slot one. Same bytes
produce a separate content pass; changed bytes produce `content_not_equal` with
no coverage record. Every case still reports three original evaluations,
`missing_tuna_resend`, `Recovered=false`, unchanged prior inputs/results and
passing full/compact parity. Positive cases select 18 witnesses, below the
unchanged 64-witness ceiling.

Each positive case passes 35 detached query controls, including exact rejection
reasons for malformed/retired evidence, wrong scope/index/length/content,
ambiguous batches, missing/late/non-dispatched receives, changed installation,
missing forwarding, superseded/terminal publication and exhausted bounds. A
detached disjoint-first-pair control tests selection only; its rewritten rows
never acquire original source custody. Three additional controls exercise real
primary custody rejection for a missing context and copied input arrays.

Nine directly affected regressions also passed: the three existing exact-payload
cases, two existing rebatching producer cases, two eight-observation lifecycle
cases and two independent Tuna-pair cases. This step therefore verified 12
distinct passing cases, each using both PowerShell hosts, without an aggregate
suite or GUI run. The original Core/Infra assembly hashes, all eight accumulated
runtime source hashes, seven prior capture test receipts, 16 original rebatching
artifact pins and 99 historical live artifact pins remain intact.

The [verification receipt](../artifacts/content-consumer-implementation-20261001/verification.json)
records the focused and directly affected regression outcomes, source/assembly
pins and preserved original artifact hashes. Earlier failed development receipts
remain available. The build initially encountered stale package paths and an
unwritable Avalonia telemetry log; cached packages and the existing telemetry
target condition resolved those setup failures without repository configuration
or runtime changes. Vulnerability-feed and existing compiler/analyzer warnings
remain; this was not a dependency-security audit.

## Remaining step and current state

This is an offline test implementation, not production integration or policy
adoption. A content pass does not waive other unresolved failures or prove
durable storage, general stale-callback safety or a scenario pass. Historical
logs cannot acquire these commitments retrospectively.

Recommended next step: review the bounded production integration contract,
including primary-owned witness acquisition on real-sized inputs. Acceptance
cutover remains a separate decision. A rebuilt targeted live run comes after
that integration and its focused checks, not a full GUI matrix now.

This step performed no portable rebuild, live NKN/GUI run, commit or push. All
accumulated work remains uncommitted on `v0.7.0` at base commit
`7ddbe15c3b894cf2497207cc39a08189b7578cc0`.
