# Canonical native source before witness binding — 2026-09-27

Status: canonical-source handoff and source-validation fix verified on both
PowerShell hosts. Scoped compatibility checks passed; native fallback-witness
binding to the actual payload pair remains unproven.

The previous checkpoint carries the actual 256 MiB pair through the original
primary and evaluation ownership, but its fixture selects only two drain
records. The apps already emit real coordinator streams and transfer events;
the first missing edge is preserving those records in the native semantic copy.

Use the existing `Get-GuiSmokeSemanticEvidenceEventNames` catalog in a dedicated
`canonical-source` fixture case. Do not alter the production catalog, selection
algorithm, native budgets, payload caps, authority evaluation count or default
loader. All earlier drain-only controls remain unchanged.

Verify an actual full-size DevLocal process tree, native physical source
origins, original full/compact coordinator evaluations, pair custody and cleanup.
Keep the primary's original failure and the parent acceptance result intact.
The current application fixture explicitly transfers over Regular v4 only;
it cannot truthfully supply the fallback grace lifecycle receipt required by
`Resolve-FileTransferNativeFrontierWitness`. Do not synthesize a failure,
receipt, witness, or runtime authority to obtain a positive witness test.

Success for this step is original canonical-source retention and an explicit
evidence-based witness eligibility result, not a completed route-cycle proof.
Live dispatch, acceptance, portable packaging, commits and pushes are out of
scope. Baseline: 360 dirty entries, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, preceding seal
`761a0c130fc8dd8765352bba64562473f8224ccb77fe11cbcd6abfa663034657`.

## First failed edge and bounded fix

The first canonical run failed during native source reading, before any
coordinator evaluation. A diagnostic-only rerun confirmed
`frontier_source_not_current`, `active_ms=20003`, `lines_read=14119`; the GUI
handoff had completed under its unchanged 10-second active-work budget.

The primary file channel and post-bind request scope still acquired mapped
memory separately for every byte on every ownership check. Replace those two
loops with fresh bounded bulk reads, then compare every original byte against
the retained packet. There is no cached approval, sampling, skipped padding,
omitted tail, new clock, increased budget or authority change. Existing state
monotonicity, cancellation, original-owner and held-file checks remain in place.

Both normal-loader mutation tests passed before the canonical rerun: every
file-channel byte (including reserved/padding bytes), every byte of small scope
packets, large word/tail boundaries, retained-packet mutations, invalid/regressed
states, cancellation and disposal. The fix is limited to these comparisons;
it does not tune transfer throughput or transport behavior.

The first comparison change was insufficient: the next full-size run still
exhausted the unchanged budget at 20,002 ms / 14,848 lines, before evaluation.
The large facts-body comparison is also repeated by each native source check.
Its live-body path now uses a bounded full comparison with both the mapped view
and retained byte array pinned during the call. Every byte is compared in
262,144-byte chunks, retaining the same intermediate work checks. Publication
and open still validate the entire zero-padding region with the existing path.
No successful comparison is cached or substituted for original ownership.

The API returns the matching prefix length, so only equality with the complete
requested length is success. See Microsoft's
[RtlCompareMemory contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-rtlcomparememory).
The implementation bounds the region against both accessor capacity and the
safe handle's buffer length and releases both pins in `finally`.

The expanded normal-loader facts tests passed on both PowerShell hosts,
including every byte of small bodies, word/read/chunk/tail boundaries of larger
bodies, retained-body mutation, full-padding checks, cancellation and closed
views. End-to-end validation remains pending.

That rerun advanced to 34,914 of 36,370 records but still reached 20,002 ms,
again before evaluation. `ReadSource` additionally scanned all custody facts
before and after `GuiSmokeLedgerLfRead.ReadLine`, even though that reader already
does the same full check before each fragment and immediately before returning
a line. Remove only those duplicate scans. The outer method keeps its immediate
liveness/work check, exact source identity, read-order and size limits; EOF
retains its independent full check. The original per-fragment and final-line
full validations remain unchanged. Test mid-read scope/facts mutation, parent
cancellation and exhausted read budget before another canonical rerun.

## Actual analyzer result after the reader fix

The next PowerShell 7 run consumed all 36,370 original semantic records and
retained native physical origins. Full and compact views both reported
`SnapshotV1Authoritative`, complete streams, 14 boundaries and zero legacy
selections. The actual payload pair bound to the original evaluation and
retired with it. The existing three evaluations / one native source read stayed
unchanged.

The primary selected **no failure** and its native diagnostic catalog contained
**zero receipts**. The parent legitimately returned `completed` with
`completedProofArtifacts=true`. This is analyzer-artifact completeness, not
acceptance of the Regular-to-Tuna-to-fallback route cycle. The fixture's earlier
drain-only `proof_incomplete` expectation was stale for this new source and has
been corrected only for `canonical-source`. It now stops explicitly before
route-cycle acceptance/reconciliation. The drain-only cases still require the
original incomplete-proof rejection.

Final canonical runs on both PowerShell hosts passed in `canonical-final.trx`.
Each consumed 36,370 records, returned complete authoritative full/compact
reports and retained the three-evaluations / one-source-read invariant. Each
stopped explicitly before route-cycle acceptance. Scoped compatibility checks
also passed.
The remaining witness gap is not a missing selector: this actual transfer is
Regular v4 only and does not contain the fallback lifecycle that the resolver
requires. The next integration must exercise an actual same-transfer fallback
episode under coordinator ownership and collect its genuine native receipt.
Existing small fallback fixtures may inform that work, but their different
transfer/session identities must not be transplanted into this 256 MiB pair.

There is also a concrete retention prerequisite for that next fixture: the
current GUI semantic catalog does not list `filetransfer_frontier_payload_marker`,
while the native resolver requires that exact marker line. The earlier small
native-witness fixtures explicitly add it. Preserve the genuine marker alongside
the canonical source when introducing the same-transfer fallback receipt, and
test its omission as a rejection control. This step does not change the live
catalog speculatively or claim marker coverage from a source with no receipt.

## Final verification

- **19 distinct final tests passed, zero failed/skipped:** two primary byte-
  comparison cases; two expanded facts-comparison cases; eight source-mutation,
  cancellation and budget guards across both hosts; two canonical full-size
  process trees; two default-loader guards; one previous drain-only full-size
  case; two original ordinary relay-analysis cases.
- Results are in `artifacts/payload-canonical-source-20260927/results/`:
  `comparisons.trx`, `source-guards.trx`, `canonical-final.trx` and
  `canonical-compatibility.trx`. Duplicate facts-test executions are counted
  once. Five earlier exploratory failures remain separately recorded: four
  native-source budget failures and the stale fixture expectation after the
  source first succeeded. None is represented as an acceptance pass.
- Seven changed PowerShell scripts parse successfully; scoped
  `git diff --check` passes. Both final source/destination pairs remain 256 MiB
  with SHA-256
  `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
- Preservation: all 360 baseline dirty entries accounted for; eight scoped
  files changed and the other 352 remain byte-identical. Six newly dirty/new
  files are in scope, for 366 current dirty entries. No unrelated changes.
- Final checkpoint:
  `artifacts/payload-canonical-source-20260927/FINAL-VERIFICATION.json`.

No live run, full matrix, portable rebuild, commit or push was performed.
The live dispatch still omits `-FrontierOwner`; route acceptance, transport
behavior, diagnostic payload caps and all work budgets remain unchanged.
