# Content diagnostic production integration review

2026-10-01. Base commit `7ddbe15c3b894cf2497207cc39a08189b7578cc0` on
`v0.7.0`; the accumulated implementation remains uncommitted. This follows the
[test-only consumer implementation](file-transfer-content-consumer-implementation-20261001.md).

## Review outcome

Do not copy the fixture probe into production or enable alternative acceptance
yet. The first integration edge is bounded, primary-owned acquisition of the
content query's observations and complete supporting history on real-sized
inputs. The small-fixture query is tested; that acquisition edge is not.

Recommended next implementation: a default-off, test-only native-primary
acquisition adapter and its focused size/custody regressions. It must establish
exact original origins, completeness and cumulative work accounting before a
production seam is added. No runtime transport change, raised limit or new live
run is needed for that slice. This review proposes that scope; it does not claim
the adapter exists or that production integration is verified.

The diagnostic remains `pre_rejection_tuna_content_coverage_v1`. Its pass means
the selected old Regular-V4 chunk's committed content was observed in an
authenticated, dispatched second-Tuna-V4 batch before the old receive was
rejected. It is not recovery, durable-storage proof or a scenario pass. Existing
strict exact-resend acceptance and all other unresolved failures remain intact.

## Source and size findings

The historical live checkpoint's retained-analysis receipt records 102,215
input lines and unchanged input hashes. Its stage diagnostics report 102,215
source events and no synthetic events. Its completed compact snapshot reports
5,524 boundaries, 3,736 effects, 9,009 operation entries and passing stream
completeness. The retained input is 173,102,783 bytes; the writer-evidence slice
is 209,235,212 bytes. These are read-only measurements of preserved evidence,
not a current runtime validation or custody capability.

| Integration gate | Current implementation | Historical input consequence |
| --- | --- | --- |
| Fixture query admission | 4,096 rows; 256 boundaries | 102,215 rows; 5,524 boundaries cannot enter |
| Query record reservation | 64 times rows plus view/effect/operation entries, per view | Rows alone reserve 13,083,520 visits across two views, beyond 2,097,152 |
| Legacy publication witness | At most 8,192 boundary/effect entries across both views | 18,520 entries exceed that gate before writer verification |
| Operation accounting | Existing 8,192 operation-row ceiling | 9,009 entries in one historical view preclude an unrestricted operation scan |

The [legacy witness resolver](../tools/FileTransferOps/LocalPrimaryPublicationWitness.ps1)
validates original boundary origins but retains only the selected failure's
publication and observed-state keys. Its prepared result cannot supply the
old sender and containing Tuna batch's independent bindings. Its writer
membership proves retained-row membership in a verified writer stream, not
that all relevant lifecycle or competing-send records survived retention.

The [test probe](../tests/nLink.SmokeTests.Core/Support/PublicationContentCoverageProbe.ps1)
requires every source line to survive into its full input in physical order.
Its publication-origin lookup also treats physical line minus one as a row
position. Neither assumption can be imported into a filtered native semantic
copy. Full, compact, semantic and original physical ordinals must stay distinct.

The GUI wrapper requests frontier custody when available under its existing
local-publication diagnostic option. The primary has separate native and legacy
branches, not interchangeable owner objects. The
[native evaluation custody](../tools/FileTransferOps/LocalFrontierPrimaryEvaluations.cs)
already binds original arrays, completed views and physical origins, but does
not expose the fixture probe's legacy context or work ledger. Individual
`GetPhysicalOrigin` calls run currentness checks over the arrays; they are not
free constant-time lookups.

The existing native `TryResolveWitnesses` operation in
[LocalFrontierPrimaryWitnesses.cs](../tools/FileTransferOps/LocalFrontierPrimaryWitnesses.cs)
is single-use, requires an owned receipt payload and delegates to a witness batch
whose failure can close native inputs. Its production caller handles a different
frontier failure and proof shape. Do not consume that slot, borrow an unrelated
payload, fabricate a receipt or change that caller to run this content query.

The [semantic event catalog](../tools/GuiSmokeEvidenceCatalog.ps1) includes
security installation/retirement evidence in addition to the existing authority
and legacy evidence families. The ledger processor copies selected semantic
families while retaining physical origins. This is a usable foundation, not by
itself proof that a particular invocation captured every required family.

## Acquisition contract

The adapter must run inside the original primary after the original full/compact
evaluations, candidate selection and ordinary reporting, and before private
custody is disposed. It selects only the already reported unrecovered
`file_tuna_data_path_mismatch` candidate. It must not choose another candidate
because the first one is inconvenient, rerun a correlator or call an evaluator.

Use the same native owner, pair, original arrays, completed views and retained
source lifetime. A legacy owner is not a substitute. The first slice targets
native custody explicitly; unsupported or absent custody returns an unproven
diagnostic. There is no silent reopen through the legacy reader or a second
analysis process. JSON summaries and previously saved hashes remain evidence,
not recreated ownership.

Acquisition has two outputs with different purposes:

1. Complete private query coverage over the relevant source families and local
   publication/security histories. It must support uniqueness of the original
   send, containing second-Tuna send and authenticated receive, and absence of
   intervening replacement, supersession or terminal publication. A sparse set
   of positive witnesses cannot establish those negative checks. Prove that the
   source's semantic selection includes every required family through its
   verified capture endpoints; missing or uncertain family coverage is unproven.
2. A union of 1–64 distinct supporting witnesses, including the selected failure,
   old send, Tuna send/receive, rejection companions, installations/retirements,
   publications and forwarding effects. Each retains its exact original line,
   writer sequence and source-line hash, with full/compact origin agreement.
   Input ordinal, semantic ordinal and physical ledger line are not aliases.
   Source-line SHA-256 never substitutes for the payload commitment.

Reuse original primary indexes where their exact reference membership can be
verified. Index keys are selectors, not authority. Candidate selection uses
declared containing range and the second publication, before commitment
comparison; ambiguous alternatives remain unproven. Inspect possibly relevant
malformed records rather than dropping them to obtain uniqueness. Never choose
the first, nearest or first equal-commitment pair.

Do not materialize a second whole-source parsed copy, serialize entire views
twice for equality or repeat a whole-history query for each view. Prefer bounded
reference-based selection over the original index and one complete-history
acquisition, followed by independent full/compact publication binding. Retain
only needed lookup keys while preserving completeness checks. This is a proposed
algorithmic reduction, not permission to exempt inspected records from charging.

All source inspection, index construction/lookup, operation inspection, origin
verification and final currentness checks must charge the original cumulative
budgets before results are exported. Keep input records/code units, line size,
producer records/code units, operation rows and the 20-second optional-work
ceiling unchanged. Do not substitute the native evaluator's separate 60-second
or evaluation-visit allowance, start a fresh allowance or reset elapsed work.
Measure the inherited work and remaining headroom. If acquisition cannot fit,
return unproven with its first exhausted bound; revisit that exact edge before
any production promotion. The table is not evidence that scoped acquisition
already fits these limits.

The adapter needs diagnostic-only native work/origin access that does not consume
or cancel the existing frontier receipt operation. Missing content, capacity
exhaustion and ordinary query rejection must not retire otherwise valid shared
custody or affect another diagnostic. Genuine owner invalidation still fails
closed. No route/publication completion capability, runtime token, payload
receipt or authority evaluation is issued by content acquisition.

## Eventual production seam and verification gate

After acquisition is verified, a production integration may add an explicit
default-off diagnostic option at the original-primary seam and a separate
versioned detached result. No test-support script loading, environment-driven
monkey patch or fixture mutation belongs in production. Keep payloads, secrets,
private arrays and live capabilities out of that result.

Require the existing content proof chain unchanged: original rejection chain,
endpoint-local publication and forwarding bindings, recorded security spans,
canonical sender suffixes and full commitment equality. A pass is exported only
after final custody, source-origin, input/result invariance and full/compact
parity checks. Any failure clears coverage. The optional result cannot modify
`Recovered`, candidate/failure counts, route state, progress, retries, warning
policy, existing diagnostic results, report verdict or process exit code.

The bounded acquisition slice should verify:

- Genuine native custody on PowerShell 5.1 and 7, with copied arrays, stale owner,
  substituted candidate/view, missing family coverage and gapped physical origins
  rejected or mapped correctly. Include unchanged default-off behavior and
  preservation of the existing frontier operation after content rejection.
- First/middle-slot same-content coverage and changed-content rejection using
  owned offline fixtures. Preserve the three original fixture evaluations and
  the strict `missing_tuna_resend`, `Recovered=false` result. Require zero added
  authority evaluations, not three evaluations for every possible production run.
- A generated offline input at least as large as the historical 102,215-record,
  5,524-boundary and 173,102,783-byte retained-input shape, with complete relevant
  histories and independently measured visit/code-unit/operation/time charges.
  In-bounds cases must select
  exact witnesses; omitted, ambiguous or over-budget cases must remain unproven.
  Unrelated history must not silently truncate relevant searches. Passing small
  fixtures or quickly rejecting every large input does not complete this gate.

Run focused acquisition regressions first, then only directly affected custody,
query and catalog lanes. Add at most one broader contract lane after production
integration stabilizes. A later portable rebuild and targeted live-256 run must
collect new sender commitments and validate the integration under strict
preflights. Historical logs have no such commitments and cannot be retrofitted.
Acceptance cutover and a full GUI matrix remain later, separate gates.

## Review verification and current state

The [review receipt](../artifacts/content-production-integration-review-20261001/verification.json)
pins this document and reviewed source seams, rechecks prior implementation and
fixture/test receipts, and preserves all 99 historical live artifact pins. It
records the measured size comparisons, not a replayed query or live verdict.
Unchanged tests were not rerun for this documentation-only step.

Production tools, runtime/tests, budgets, acceptance and historical verdicts are
unchanged in this step. No build, GUI/NKN run, commit or push was performed.
