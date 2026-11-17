# Retained native source across runtime successors — 2026-09-27

Status: focused verification complete. No production/runtime authority,
native analysis budget, acceptance, or live-dispatch changes.

Baseline: HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`,
369 dirty entries. All 522 pins in the preceding fallback-completion checkpoint
matched before work. That checkpoint's SHA-256 is
`cf15b88a8d8cf4f3ba2b477a94bba1e38e3eb17a7b2129db3a9f0c0f8e326670`.

## Bounded implementation

Add a separate test-assembly-only reader factory to `AttemptSenderSourceChannel`.
The original one-shot and two-read paths keep their limits. An atomic mode
claim prevents mixing either legacy path with the new factory, including a
race with the legacy interlocked one-shot claim. The PowerShell profile and
default loader do not compile the new factory.

Binding requires the original source, original app lease, original app-owned
service, matching app lifetime, and that service's actual outbound context.
The context is captured directly from the service, not supplied as a snapshot
or an authority callback. Binding once does not grant a read.

The factory permits only these four reads:

1. Metadata, at the actual `PreparingMetadata` boundary.
2. Regular v4 payload reader, with the same coordinator owner as metadata.
3. Tuna v4 successor, with the next runtime generation and route epoch.
4. Fallback v6 successor, with the next runtime generation and route epoch.

Each payload reader requires the actual pending installation and its exact
data session. Every reader retains its original owner/publication; payload
readers additionally retain the exact installation object as it moves from
pending to installed. Existing coordinator validators and the service's
installation validator must still accept them. Context replacement, lifetime
cancellation, owner/publication replacement, native-source closure, or disposal
reject further use. Copied factories/readers cannot read or dispose the
original view.

`ReOpenFile` operates on the original held native handle only. No pathname,
PID lookup, new file selection, or replacement metadata supplies the source.
File identity and exact 256 MiB length are rechecked. The reader exposes no
raw file handle, checks currentness before and after each read, and clears
read bytes if the post-read check rejects. These checks grant no dispatch,
route, progress, or publication authority.

## Actual integration and corrections

The original GUI fixture delivers its held source to the original helpee app
through the existing native stdin channel. Inside that app, the existing
production Core/NKN route fixture uses a fake offline wire. It performs ordinary
Regular-to-Tuna activation and Tuna-to-fallback stop, with the original
coordinator publication/disposition assertions. Metadata hashes all 256 MiB.
Payload reads stay gated until fallback publication; the test then compares
received bytes with bytes read from the native source and retires the contexts.

The first run rejected Tuna's reader because the prototype required physical
disposal of the old stream before opening its successor. Core intentionally
allows retired-pump cleanup after successor open. The factory now closes the
old source view only after a distinct next coordinator owner and its pending
installation validate and the replacement native file opens successfully.
Same-owner overlap, replay, and rejected opens cannot retire the old view.

Both positive host cases then passed. Closed/disposed-anchor controls rejected
the fourth read correctly but the fixture returned before Core finished
delivering its failure, causing a diagnostic write after final log drain.
The negative branch now joins the actual `read_failed` terminal result on both
sides before shutdown. The strict writer drain/adoption contract is unchanged.
No delayed sleep, fake receipt, suppressed log, or relaxed shutdown check is
used to make the control pass.

Compatibility verification also found a separate race in the previous
file-backed completion fixture: `Completed` can be observed before the
destination writer is disposed. `File.OpenRead` correctly refused to overlap
that writer. The fixture now awaits an actual destination-disposal task before
reopening for its hash check. Sharing modes remain strict; there is no retry
loop, sleep, or production completion change. Only the two affected file-size
cases are rerun after this correction.

## Verification and preservation

The latest result for each of 13 distinct focused tests is passing, with no
remaining failure or skipped case:

- `runtime-reads-verified.trx`: four native successor-reader cases (valid under
  PowerShell 7 and Windows PowerShell, plus closed/disposed-anchor rejection).
- `compatibility.trx`: seven passing legacy/default-loader/production-route
  cases. Its small-file failure is superseded by the corrected rerun below.
- `destination-close.trx`: both corrected file-completion cases pass, at
  64 KiB and 256 MiB. The 256 MiB destination SHA-256 is
  `dc97b68b1ad220409ed99f5bfd029c86607ca2a9ba9732cc2688e0e744f52fe3`.

These TRX files are retained under
`artifacts/payload-runtime-reads-20260927/results`. Four earlier failed test
executions and four intermediate passing executions are retained separately;
they are not counted as additional final tests. An initial CS0411 compilation
error was corrected before those executions and is not a test result.

The scoped change comprises eight files: five previously dirty test/support
files, two new test/support files, and this report. Of the 369 baseline dirty
entries, 364 remain byte-identical and five have scoped modifications. Three
new entries bring the total to 372. No unrelated baseline change, production
edit, native budget adjustment, acceptance change, or default-loader change
was made. Scoped whitespace checks pass.

The evidence and preservation checkpoint is
`artifacts/payload-runtime-reads-20260927/FINAL-VERIFICATION.json`. It records
the current dirty inventory, source/artifact hashes, previous seal, and exact
latest test results without relabeling exploratory failures as successes.

## Scope of proof and next step

This closes the native successor-reader prerequisite, not the full native
source/destination/witness integration. The receiver in this new fixture is
still in the helpee process and uses an in-memory destination. Only bounded
fallback data is received; the prior file-backed full-completion test is a
separate result and is not substituted for this native source.
The original primary was not invoked; no native cross-process fallback pair
or genuine selected failure witness is claimed by this checkpoint.

Next, provide an offline cross-process production-route carrier while retaining
the existing exact peer-process/pipe checks and native destination evidence.
Then obtain a genuine same-transfer frontier lifecycle receipt and marker for
the original primary to select. No synthetic failure/witness or unrelated
receipt is acceptable. The second Tuna publication required by the live route
cycle remains outside this step.

No live run, full matrix, portable rebuild, commit, or push was performed.
