# Native membership scan profile and bounded batching fix plan

2026-10-02. The fresh offline profile identifies repeated full resource
validation as the initial membership scan's dominant cost. Selection inherits
18.25 seconds of active work and exhausts the unchanged 20-second allowance
after completing only 3,633 of 102,218 rows. Resource checks account for 99.5%
of the measured scan interval. The acquisition gate remains failed; this
step adds diagnostics and a review proposal, not a validation optimization.

## Diagnostic scope and controls

Selection profiling is an explicit test-loader option. Its helper and timing
hooks compile only under a separate test symbol. The existing scale-profile
fixture now selects this membership profile; its source-reader instrumentation
is off. Explicit small source-profile cases retain their previous behavior.
No attachment, exit, cleanup, active-work, record, or code-unit limit changes.

The profile emits at most two bounded records, at entry and return, while the
original cumulative work clock is stopped. It reads passive fields, counts
completed rows, and times existing checks. Timers run inside the existing
active interval, so their overhead remains charged rather than subtracted.
Each resource wrapper calls the original `ResourceCurrentWithin` exactly
once, with the original allowance and short-circuit placement. It returns
the original result or exception and never caches currentness. Removing the
profile compilation blocks reproduces the exact unprofiled acquisition body.
No source pass, authority evaluation, clock reset, or validation call is added.

Twelve distinct controls pass: two explicit-loader controls, six membership
success/rejection controls, and four existing normal/source-profile controls.
One new record-boundary assertion initially expected zero resource calls.
The existing code correctly performs two checks before rejecting admission
at the record ceiling. That test expectation is corrected, only that case
is rerun, and the initial failure is preserved as a development assertion
error rather than an application failure. Core and Infra assemblies remain
byte-identical; the profile helper types are absent from normal assemblies.

## Fresh scale observations

One selection-profile scale fixture runs. Original source completeness,
materialization, evaluation recording, pair issuance, and default-off behavior
pass before selection. At its entry, the stopped native clock contains
18.2497104 seconds of inherited work, leaving 1.7502896 seconds of the
producer allowance. The source has 102,218 lines and original evaluation
visits total 613,312.

| Measured selection interval | Seconds |
| --- | ---: |
| Newly charged active work | 1.7517314 |
| Initial membership scan | 1.7508274 |
| All cursor checks within that scan | 1.7451081 |
| Original full resource checks within cursor checks | 1.7424654 |

These intervals are nested and must not be added together. The profile
contains one incomplete scan, 3,633 completed rows, and 14,536 cursor checks
and resource-check calls. It has reached 3.55% of the first full scan; the
selector callback and second scan have not run. The call returns `unproven`
with `content_input_budget`, 616,946 record visits, 208,731,935 code-unit
visits, 20,001 cumulative milliseconds, zero witnesses, and zero callbacks.
Record and code-unit ceilings are not exhausted. The native source remains
complete and the recorded closed flag remains false at the return snapshot.

Both profile records observe the clock stopped. Both streams complete without
truncation. The parent records no deadline and fails at output validation,
not at its unchanged 90-second attachment wait. Earlier processing and the
208,326,025-byte semantic copy succeed, and semantic/analyzer actual hashes
and 102,218-line metadata match. None of these offline observations supplies
content-family coverage, route authority, or live acceptance.

## Cause and proposed contract review

Each completed event row invokes full resource validation four times: before
and after its record visit, and before and after charging its path units.
The adapter requires an entire membership scan both before and after primary
selection. In this first, incomplete scan, full resource checks consume
99.5224% of measured scan time. Optimizing event property lookup alone cannot
recover the missing headroom shown by this profile. The composite check's
individual native predicate costs are not separately measured here.

A linear projection of this diagnostic prefix gives about 49.3 seconds for
one full scan and 98.5 seconds for two, before adding inherited work or other
selection operations. These are estimates, not completion bounds or an
unprofiled benchmark. They nevertheless show why a small shared-limit
increase is not a demonstrated fix. The source and transport paths should
not inherit a blanket increase in optional diagnostic allowance.

Recommended next step: review a bounded private-batch validation contract.
Define which budget, membership, mutation, and cancellation checks must remain
per row, and whether full fresh resource validation may occur at bounded
private-batch boundaries and before any result is exported. Address native
invalidation during a batch, change-and-restore cases, rejection semantics,
and final before-and-after selection invariance explicitly. Internal locks
alone do not establish native scope freshness. No previous successful check
may become cached authority, and no partial scan may publish a result.

Only an approved contract should lead to a surgical frequency reduction in
the test-profile adapter. Keep all inspected rows, original references and
physical origins, cumulative charges, the 20-second cap, default-off behavior,
and coordinator authority intact. Verify invalidation and mutation at each
proposed boundary, plus existing time/record rejection and custody-retirement
controls, before one normal scale rerun. If batching cannot preserve the
required guarantees or fit the budget, review a separately scoped allowance
with an affected-consumer inventory rather than changing the global constant.
No batching implementation or limit revision occurs in this step.

The [verification receipt](../artifacts/native-selection-scan-profile-20261002/verification.json)
pins the fresh profile, source checkpoint, and focused results, preserving
96 prior scale artifact pins and 99 historical live artifact pins. Production
integration and content coverage remain incomplete. No live GUI/NKN test,
portable rebuild, full matrix, commit, or push occurs.
