# Selected-failure and terminal/payload integration boundary

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-selected-membership-borrower-20260926.md`.

## Decision

The next integration cannot be implemented by enabling the two existing test
flags together. Preserve the mixed-profile refusal and the pre-verdict
selected-failure reservation guard until the native witness handoff is
implemented and verified. This step adds an executable regression for that
refusal and specifies the bounded handoff below. It does not implement the
joined positive or alter acceptance.

All 14025 pins from the preceding milestone matched before this review.
These are current source findings, not a runtime failure inferred from an old
GUI artifact. No live run is needed to establish these code boundaries.

## First missing edge

The original selected-failure witness must acquire its writer from the
already-adopted collector handle, perform its original single EOF membership
pass, and retain that exact membership for later non-owning terminal use.
The two present paths do not provide this handoff:

- `PublicationLivePrimaryWorker.ps1:5` rejects simultaneous borrower and native
  terminal-file profiles before loading either native helper.
- In `LocalRetainedSourceRequest.cs:564`, the terminal compilation branch takes
  precedence over the borrower branch. Merely compiling both symbols retains
  the collector position map but never calls the borrower's retention hook.
- The original witness opens its writer at
  `LocalPrimaryPublicationWitness.ps1:68`. The ordinary
  `Worker.TryOpenWriter` opens the descriptor path. Its full-Frontier refusal
  must remain; the test terminal-file profile is a different ownership path.
- `TerminalCollectorMembership.cs:59` acquires that same one-shot slot and
  verifies index zero, which is a membership lookup, not a selected failure.
  Its result's `Dispose` closes the reader. Neither behavior can be copied into
  a borrower of the original witness's reader.
- Collector origin and payload work currently starts its accounting from
  `7 * full + 4 * compact + membership.RecordVisits`. A selected witness has
  additional original work retained by `NativeMembership.BorrowerRecords`
  and `BorrowerUnits`; using the collector formula would omit that work.

No slot reset, second verification, replacement failure, path reopen, or
disconnected successful-result Boolean can bridge these edges.

## Bounded implementation contract

### 1. Native writer adapter for the original witness

Use a new explicit test-only combined profile, not an unconditional removal of
the current refusal. Keep ordinary borrower, ordinary collector and production
loader behavior unchanged. A checked in-memory observer seam may replace only
the original witness's writer acquisition call for that combined profile.
Do not move witness/verdict calls or pass the primary's private custody to an
evaluator/resolver helper.

The adapter must require the exact original worker, completed source and
adopted collector owner, bound to the original primary inputs. It must decline
full Frontier-native requests, stale/copy owners, absent collector custody and
an already-used writer slot. Duplicate only the adopted writer handle, checking
its existing identity/length descriptor; do not reopen the path or pre-hash it.
The original witness must still select its actual failure before acquisition
and perform the original EOF/hash, provenance, parity and selection checks.

In the combined test build, retain both position-map and borrower state on
the same successful original membership object. Bind it only at the existing
successful witness return. An unsuccessful witness must retain its original
omission/cleanup behavior and cannot be rescued by collector verification.

First prove this native acquisition on the small seeded-failure fixture using
its own collector outputs. That is an adapter/ownership test only; no completed
transfer, terminal join or real-GUI capability follows from it.

### 2. Non-owning post-witness terminal consumer

After original witness/completion preparation and reports, require the exact
current borrower and its original worker, reader, custody, evaluations,
context, failure, arrays, views and clock. Also require collector and payload
owners attached to that same worker/request/source. Detached JSON must remain
output-only. A foreign owner with equal bytes, labels or paths is still foreign.

Reuse the private original membership map to join the exact source coordinates
to collector producer/writer origins and the two held-file terminal records.
Do not require the selected membership index to be zero. Do not assign another
reader to the original context. A terminal consumer's disposal must invalidate
only that consumer; original context/worker cleanup closes the reader and
permanently invalidates every borrower. Failed publication completion must not
leave a cached borrower usable after custody has been retired.

Start from the original witness's actual cumulative visits/code units, adding
only new borrower/origin/terminal work. Resume and stop its original clock;
never replace/reset it or count the EOF pass twice. Keep the existing 4096
visit, 20-second, code-unit, payload and input-size caps. Reject insufficient
remaining budget before a bounded traversal. Historical counters do not grant
current access.

Keep this join post-report. It cannot supply the earlier verdict or remove the
pre-verdict reservation guard. Any later ordering change is a separate design
step, not a consequence of successful post-witness coexistence.

### 3. One actual transfer producer

The current fixtures are not an integration positive: the seeded publication
fixture has no completed payload, and the real 256 KiB Regular-v4 fixture has no
selected publication failure. Putting their logs in one directory or running
both in one process would not make their coordinator/transfer ownership equal.

A joined positive must originate in one private transfer owner, with its actual
Service/coordinator publication, original source log, selected failure and both
real completed terminals/payloads. A controlled failure observation must come
from that owner's transfer and current publication evidence before its logger
drains. It must be explicitly labeled seeded if test-authored; do not append
records after finalization, invent writer metadata, reuse the standalone seeded
coordinator under matching IDs, or transplant another transfer's evidence.
Keep fixture producer construction distinct from the original primary's three
authority evaluations. Do not add an analyzer authority evaluation for the join.

If no such same-transfer producer is available, report that missing edge and
retain omission. Do not claim a positive from the separate adapter fixture.
The canonical live-256 parent scope, two independent GUI writers, continuous
real-GUI payload handoff, and five-route cycle remain outside this prototype.
Equal payload hashes do not make any failed route gate pass.

## Required verification for implementation

Run each new focused test first on both PowerShell hosts. Then run only the
affected original selected witness/completion and collector origin/payload
tests once; avoid analyzer/contract aggregates and the GUI matrix.

- Exactly one original writer EOF pass, zero additional writer reads or
  primary payload hashes, and the original three authority evaluations.
- Unchanged original witness/completion, reports, input/view references and
  failed independent route result.
- Rejection of copied/foreign worker, reader, membership, collector/payload
  owner, custody, evaluation pair, context, failure, arrays/views and clock;
  wrong source/writer/producer coordinate; expired/disposed resources; stopped
  clock; exhausted budgets; repeated acquisition, issue, join and consumption.
- Borrower/terminal-consumer disposal leaves the original reader and completion
  current; original cleanup and failed completion make borrowers unusable.
- Ordinary collector behavior, ordinary borrower behavior, production API
  absence and full Frontier-native refusal remain unchanged.
- A same-transfer positive plus an equal-byte foreign-transfer rejection is
  required before calling terminal/selected-failure integration complete.

## Verification in this step

The new mixed-profile boundary regression passes on Windows PowerShell 5.1
and PowerShell 7: two cases, zero failures or skips. The test project build
succeeds with 257 warnings and zero errors. Results are in
`artifacts/selected-terminal-integration-20260926/results/integration-boundary.trx`.
The regression invokes the actual test worker with both profiles enabled and
requires the exact refusal before native types, analyzer functions or an
analysis artifact directory exist. This is deliberately a negative boundary
test, not a transfer or successful joined-consumer test.

Only the existing borrower test partial and this new review document change.
No runtime, native helper, observer, loader, acceptance or pre-verdict guard is
changed. No portable build, network/GUI run, live-256 test, full matrix, commit
or push is part of this step.

The verification seal is
`artifacts/selected-terminal-integration-20260926/FINAL-VERIFICATION.json`.
It references the previous seal, records the two expected prior-pin changes
(the test partial and rebuilt test DLL), and pins this step's files/results.
