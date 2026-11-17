# Test-only pre-Send source custody

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-attempt-writer-binding-20260926.md`.

## Outcome and limits

The explicit attempt profile now holds a real 256 MiB prepared source file
from the GUI's post-creation, pre-Send hash seam through genuine app writer
shutdown and closure matching. Its original attempt and named helpee request
must be current at acquisition. A later one-shot association requires that
same attempt's original closed Core writer pair.

This closes the **GUI-local prepared-source hold** prerequisite only. The
fixture does not click Send, open this payload in the sender Service, transmit
its bytes, or create a received destination. The existing Core children still
register the earlier 1 KiB awaiting-acceptance/reset producer. Their writer
closures are genuine, but associating this source with their app identities
does not establish a same-transfer producer or terminal association.

No live eligibility, integrity result, publication completion, acceptance
permission or route authority is issued. This is not a live-256 result or a
claim that the whole transfer-file custody gate is complete.

## Implementation

`FrontierAttemptSource.cs` is compiled only by the explicit
`NLINK_TEST_ATTEMPT_DISPATCH` loader. It reuses the existing native
`LocalInputReadLease` implementation without changing its production source.
The source is opened read-only with read sharing only. Length must be exactly
268435456 bytes before hashing; the original held stream supplies the hash and
native file identity. Subsequent availability checks reopen by retained native
handle, not pathname. The source owner exports no stream or handle.

This is separate from the diagnostic payload catalog. No diagnostic payload
cap, log-source cap, source-processing budget or analyzer budget is raised,
reset or relabeled as transfer capacity.

The attempt admits one source acquisition. Wrong role, copied request and
copied attempt candidates are refused before acquisition. An admitted but
failed acquisition remains spent. The source owner has no public constructor
or setter; copied owners cannot read its current diagnostics or dispose the
original. Its closed-writer association likewise requires the exact retained
join and can succeed only once.

Observed cancellation, a lost held handle or a retired writer join retires the
source and clears its diagnostic projection. Retirement cannot revive when
test state is restored. Original attempt disposal closes its source under the
same role lock, including acquisition/disposal overlap. Scenario cleanup also
disposes the source on exception paths. No new cleanup thread is introduced;
underlying invalidation is observed by access checks or normal owner cleanup.

The GUI fixture extracts the original deterministic payload-creation statement
and pre-Send hash statement from the original cycle AST, checks their ordering
before the original Send statement, and substitutes the owned hash only in
that test copy. It does not execute the rest of the cycle. The existing live
Context-only dispatch guard remains in force. Only the fixture receives the
private attempt/source arguments, never the normal scenario context or reports.

Custody begins at the restrictive post-creation open. It does not establish
identity continuity during file creation or the gap before that open. The
plain measurement file is output-only and cannot reconstruct custody.

## Verification

Both initial host positives passed. After the acquisition/disposal lock review
and addition of independent post-release hash corroboration, all 14 focused
tests passed on Windows PowerShell 5.1 and PowerShell 7:

- Original source hold and association with the genuine closed writer pair.
- Parent cancellation, lost source handle and original attempt disposal.
- Wrong source length and a concurrently open writer, with no acquisition retry.
- Default-loader isolation on both hosts.

The native cases also exercise wrong-role/copied request and attempt refusal,
copied source/join refusal, repeated acquisition/binding, no revival and no
authority flags. Positive holds deny writable opens and renames
while held, both before and after original app shutdown. After retirement,
exclusive read/write open and rename/restore succeed. All eight final positive
holds retain exactly 256 MiB; their reported full SHA-256 values are independently
corroborated after release, not used as ownership input.

Final focused results are in
`artifacts/frontier-attempt-source-20260926/results/focused-final.trx`.
The initial two positives are repeats, not additional distinct tests.
The single compatibility run also passes all eight cases: both prior writer
positives, both reversed-adoption cases, both writer default-loader controls,
and both role positives. `compatibility.trx` records that run. There are 22
distinct final passing tests, zero failures and zero skips. No aggregate lane
was run, and no source changed during or after these final two runs.

PowerShell parsing passed on both hosts, and touched-file/tracked diff
whitespace checks passed. All 407 preceding seal pins matched before editing.
Only four pre-existing test/support files change; the other 232 pre-existing
dirty files are unchanged. Four new test/support files and this report bring
the dirty-entry count from 236 to 241. Of the preceding seal's pins, 404 remain
unchanged; its three changed pins are the explicit loader and writer fixture
hooks. The fourth edited file is the older test-only attempt implementation.

No production source, coordinator behavior, acceptance rule, selected-failure
pre-verdict reservation, portable package, live GUI/NKN run, full matrix,
commit or push is changed or performed. The final incremental seal is
`artifacts/frontier-attempt-source-20260926/FINAL-VERIFICATION.json`.

## Next boundary

Bind this held source to the actual helpee Service source-read operation in
the owned attempt. Do not infer that association from the path, same bytes,
writer closure or terminal tokens. Then establish the receiver's app-owned
destination handoff using a separate transfer-payload channel.

The destination requires particular care: `SessionRuntime` supplies the
authorized `FileTransferWriteHandle` to the receive destination, but
`FileTransferWriteHandle.FinalizeAsync` closes the exclusive temporary-file
stream before moving its pathname to the final name. A later final-path open
cannot prove continuous identity across that gap, and retaining the current
exclusive file object would prevent the pathname move. Review that native
finalization boundary before proposing runtime changes; do not substitute
terminal/path matching or another post-finalization-only fixture.

Continuous GUI/wrapper/parent/analyzer adoption and original full-native
selected-witness consumption remain later gates. The required second
coordinator-published `file_tuna_v4` and the prohibition on active
`file_tuna_v6` remain unchanged.
