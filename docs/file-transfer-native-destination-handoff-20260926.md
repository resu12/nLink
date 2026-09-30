# Original-app native destination handoff

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows the completed fallback-startup checkpoint fix and step 2 of
`file-transfer-destination-finalization-review-20260926.md`.

## Bounded implementation

The actual guard-created destination can now move its existing private,
read-only native hold to an explicitly requested original-app evidence owner.
The wrapper does not reopen a path or export a write/delete-capable handle.
Public stream constructors cannot confer guard-owned custody. The production
SessionRuntime factory uses the internal guard-owned wrapper, but does not
request longer retention for ordinary downloads.

Only an original app-created Service with the exact installed inbound
destination can request evidence. The original FrontierAppLifetime and its
current native AppLease admit at most one destination request per app attempt.
Native lease validation is outside the Service gate; pending installation is
then rechecked against the same current receive context and stream. A failed
or retired reservation cannot be revived or replaced with a second request.

The existing finalization-source guard remains authoritative for the runtime
continuation. Only the source-validated Completed transition can move the
read-only storage hold, after recording completion and before normal resource
cleanup. The handoff method itself requires the held Service gate, its current
Completed context, and that context's exact pending evidence owner; it derives
the tuple/hash/length from that context rather than accepting caller metadata.
Cancellation/failure cleanup retires a pending evidence request.
No route, retry, repair, progress, liveness or acceptance authority is issued
by the storage handoff, and failure to retain evidence does not manufacture a
transfer result or change normal completion behavior.

The completed owner permits one sequential read, requiring the exact Service,
destination, and original app lease. It measures native identity and length,
maps that same read-only handle, and checks lease/lifetime validity on reads.
This avoids rebinding an asynchronous handle's I/O completion port. No path
import, diagnostic catalog size cap, full-file byte-array API, or public native
handle getter is introduced. The test fixture independently hashes through
this reader; the stored Service hash alone is not a new integrity proof.

Copied Service/destination/app/evidence/reader objects cannot exercise the
handoff, and copying an evidence owner or reader does not let its disposal
close the original. Reader disposal, explicit evidence retirement, invalid
native custody, and original-app shutdown release the retained resources.
After completed custody has moved, ordinary Service reset/disposal does not
reassign it to another transfer: it remains immutable evidence of that exact
completed operation, bounded by the explicitly admitted native attempt.

## Verification scope

The new offline fixture runs in the original **helper** child and uses a real
app-created receiver Service, an authorized guard-created file, and an ordinary
loopback sender in that same child. Its payload is 2 MiB + 23 bytes. This is not
a 256 MiB original-helpee-to-original-helper transfer and not GUI/NKN evidence.
The prior native process/output/writer ownership gates still execute; parent
analysis and reconciliation remain their existing explicit fixture stand-ins.

An instance-local, null-by-default task observer lets tests join the actual
finalization continuation before asserting stale-source rejection. Reset is
not allowed to satisfy that rejection oracle. The observer supplies no data,
authorization, finalization result, or route authority.

The 14 focused cases passed on Windows PowerShell 5.1 and PowerShell 7:
original native identity and full bounded-payload hash, pending retirement,
receive-lifetime cancellation, destination replacement, data-session generation
replacement, a closed retained handle, active-reader disposal, and shutdown
release. Positive cases also exercise copied/foreign owners, copied leases,
copied readers, duplicate requests/reads, and denied pathname deletion while
evidence is held. No in-flight mapped-read/dispose race or parent-process kill
was fault-injected; disposal is serialized with each mapped read.

The added early-handoff probe initially used an incorrect `Verifying` status
assertion: this route reports `Receiving` during finalization. Both
`completion-boundary-red.trx` (one failure) and
`completion-boundary-final.trx` (two failures) record this **fixture error**, not
an expected-red proof of the handoff bug. Their filenames are not verdicts.
After checking finalized/nonterminal state instead, the tightened completion
boundary passed on both hosts (`completion-boundary-verified.trx`).

## Compatibility finding and bounded correction

The one relevant compatibility lane initially passed 50/51 cases. The app
runtime round trip failed on immediate file access/cleanup after observing
Completed. Its ordinary native read hold was released only by later terminal
resource cleanup, after `RaiseTransferChanged`. Thus a consumer could see
Completed while the saved file was still exclusively held.

The guard-owned Service round trip was strengthened to open the file
exclusively from the synchronous Completed event itself. It reproduced the
sharing violation deterministically (`completed-access-red.trx`, one failure),
without sleeps or weakening the existing app test.

The completion boundary now drops only the guard's remaining finalized read
hold before releasing the Service gate. An explicitly transferred evidence
hold is no longer owned by the guard and is preserved. Native transfer/release
use an atomic exchange, so a read handle cannot be both handed off and closed
as the ordinary hold. Remaining terminal cleanup and non-guard destinations
are unchanged. This makes ordinary app completion observable together with
file availability, without extending retention to every download.

`completed-access-final.trx` passed all four targeted checks on the final
source: deterministic Completed-event file access, the unchanged app-runtime
round trip, and both native-helper positives (including early-handoff rejection
and retained-file protection). The 51-case compatibility lane was not repeated.
Together with its other 50 passing cases and the 14-case focused lane, there
are **65 distinct applicable passing cases**, zero remaining applicable
failures and zero skips. Overlapping positive reruns are not added to the count.

The compatibility lane covered the 19-case completion-boundary group, 17 native
destination cases, 11 authorization/storage guard cases, two app-runtime cases,
and two prior original-helpee retained-source payload-read positives. All runs
used offline transports; the app-runtime receive root was scoped to the
workspace and restored afterward. Build and whitespace checks pass. Initial
build errors (an insertion in the wrong directional method and a test helper's
base-class access) were corrected before executing the focused cases.

## Checkpoint preservation

All 112 preceding seal pins matched before implementation. Eight of those
files change intentionally; the other 104 remain unchanged. The fresh
262-file dirty baseline is preserved: six pre-existing dirty files change
intentionally and the other 256 retain their bytes/status. Four previously
clean tracked files, two new source/test files, and this report bring the dirty
count to 269. Branch and HEAD are unchanged. The evidence seal is
`artifacts/frontier-attempt-destination-20260926/FINAL-VERIFICATION.json`;
development failures remain recorded rather than erased.

## Remaining edge

Connect the original helpee's retained source and original helper's actual
receiver across the two owned processes, then verify full 268435456-byte
delivery and independent destination hashing with continuous native custody.
The native destination handoff here has no cross-process export/adoption yet.
Only afterward should GUI/parent adoption and selected-witness consumption use
this destination evidence. The second coordinator-published `file_tuna_v4`
requirement remains unchanged; no live-256 readiness is claimed.

No portable package, live GUI/NKN run, full matrix, commit, or push was performed.
