# Offline fallback file completion — 2026-09-27

Status: 64 KiB and 256 MiB file completion verified offline, with both original
route-test branches passing. This is a file-backed capability test, not the
original cross-process native payload/witness join.

## Scope and first missing prerequisite

The canonical-source checkpoint is intact: all 482 pins matched, HEAD is
`a653d4063070235c71fe211480aeb37863d9b2a4`, and all 366 baseline dirty entries
were accounted for before edits. Its DevLocal transfer is Regular v4 only;
DevLocal has no Tuna activation provider. The small frontier-boundary fixture
uses a different transfer and an in-memory destination. Neither can supply a
genuine fallback receipt for the original 256 MiB pair by relabeling evidence.

The existing `RunJoinedBootstrapCorrelationAsync` fixture offers a useful
earlier capability check: production Core and NKN transport, fake local wire,
ordinary acceleration request and stop, and coordinator-published Regular v4
to Tuna v4 to fallback v6 transitions. It previously cancelled after fallback
publication without checking payload completion. No live client is connected;
the real adapter instances serve only the existing pressure-command backend.

Extend that fixture with optional real source/destination files and verify:

- The same session and transfer complete on both sides on fallback protocol 6.
- Exact byte counts, source/destination SHA-256, and both completion hashes agree.
- Metadata and the three successive runtimes use four source opens in total;
  there is one destination open.
- Payload reads remain held until both actual fallback publications and their
  existing coordinator disposition/receipt assertions pass.

Existing non-file branches retain their behavior and eight-second route-edge
waits. No authority, receipt, route state, or failure is seeded into the new
file branch. Production runtime, analyzer, semantic catalog, live dispatch,
native custody, acceptance and portable package are untouched.

## Observed fixture corrections

The first build could not write Avalonia's default telemetry log in the
sandbox. `-p:UsedAvaloniaProducts=` skips only that telemetry target. Normal
compilation and tests still run in the sandbox. The first two test attempts
could not persist their operational log because the default path was outside
the workspace; the second attempt's redirect omitted the required artifact
root. They are retained as failed attempts, not product evidence.

With all four existing log environment variables set to a workspace directory,
the actual failure was clear: the new file factory incorrectly inherited the
Regular-only fixture's two-open assumption. Tuna's successor runtime requested
a third reader, triggering the test's `Assert.InRange` and `read_failed` before
fallback publication. Permit exactly metadata plus the three expected runtime
readers in this new fixture and require four opens at completion. This does not
change `AttemptSenderSourceChannel`, whose original two-open contract remains.

The 64 KiB case then completed with matching hashes. The initial 256 MiB run
used the old small-test forty-second deadline and was cancelled after writing
119,454,720 bytes. Its last records show continuing contiguous writes, not a
stall. Only the new full-size file case gets a 180-second correctness deadline;
all existing small tests retain forty seconds and all route-edge waits retain
eight seconds. There is no minimum goodput requirement, transport tuning,
native work-budget increase, or acceptance-rule change.

## Remaining integration

These files are not the original held native handles and carry no app/GUI/
parent/primary custody. They must not be substituted into the earlier native
pair. The two-open retained-source fixture needs a bounded, coordinator-current
successor-reader path before a same-transfer route episode can run through it;
blindly increasing its counter or reopening a path would not prove that join.
The cross-process fake transport also needs to preserve the existing exact
peer-process/pipe identity checks while exercising the production route path.

Only after those prerequisites can a genuine same-transfer frontier lifecycle
receipt and payload marker reach the original primary. The primary must select
its own real failure/witness. Successful fallback completion alone does not
establish witness eligibility, native transfer-integrity authority, publication
completion authority, or the live route cycle's second Tuna publication.

## Final verification

- Four distinct final tests passed with zero failed/skipped: the 256 MiB case
  in `results/full-completion.trx`, and the small-file case plus original
  negotiate-only and publish-fallback branches in `results/compatibility.trx`.
- The full-size test completed in approximately 83 seconds. Both files are
  exactly 268,435,456 bytes, SHA-256
  `dc97b68b1ad220409ed99f5bfd029c86607ca2a9ba9732cc2688e0e744f52fe3`.
  Directory: `artifacts/payload-fallback-completion-20260927/268435456-6c9b6c9445a94601a116dedf1c702f38`.
- The final small case is in `65536-fb5efc8f9317439ba2ef7597cd4e3c0b`, with
  SHA-256 `66c5c97c4b7f4955da7b57a1226d9429e667402c3d1a5fc2375e230391e43006`.
- Four exploratory test failures are retained separately: two unavailable-log
  attempts, the diagnosed two-reader fixture error, and the inherited small-test
  deadline cancellation. The intermediate small-file pass is also retained;
  it is not counted twice in final totals.
- No native primary or witness resolver ran in this step. The successful log
  contains no frontier payload marker or grace-recheck event; successful file
  completion is not evidence that a diagnostic witness exists.
- Only two test files and this report changed. The 366 earlier dirty entries
  and all 482 checkpoint pins remain unchanged. There are 369 dirty entries
  after these three newly dirty files. Scoped `git diff --check` passes.
- Evidence and preservation checkpoint:
  `artifacts/payload-fallback-completion-20260927/FINAL-VERIFICATION.json`.

No live run, full matrix, commit, push, or portable rebuild is part of this step.
