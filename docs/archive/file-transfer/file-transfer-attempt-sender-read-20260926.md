# Test-only held source to actual sender metadata read

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-attempt-source-custody-20260926.md`.

## Outcome and boundary

The original helpee child now receives a read-only native handle to the GUI's
held 256 MiB source. A real `SessionFileTransferService.CreateForApp()` instance
uses that handle in its accepted-transfer metadata read, reads exactly
268435456 bytes through EOF, and computes the same full SHA-256 as the GUI hold.
The original app request, process identity, file identity and normal writer
shutdown/report gates remain required.

The fixture stops by resetting the Service at its original `AwaitingStart`
metadata event. It verifies one source-factory invocation, closed read stream,
the exact Service session/transfer snapshot, and no V4/V6 chunk-batch dispatch.
It does not run the payload pump, transmit the file, create a receiver
destination, or complete a transfer. The peer acceptance is an offline
loopback transport operation, not an NKN or Tuna session or real GUI click.

This closes the first actual sender **metadata-read** edge, not the complete
sender/receiver payload path or live-256 readiness. The pre-existing 1 KiB
producer fixture remains; the new positive adds its own real Service producer.
No terminal or selected-witness same-transfer association is inferred from
the shared writer closure.

## Ownership path

`FrontierAttemptSenderRead.cs` is an explicit attempt-profile extension. Only
the original current source and exact helpee launch request can deliver once.
It uses the already-retained native child process handle, not a PID lookup,
and the original process's redirected stdin. A fresh read opened by the held
source handle is duplicated read-only into that child; neither side reopens
the source by pathname to establish handoff custody. The GUI retains its
original restrictive hold throughout the child read and subsequent shutdown.

The new bounded 512-byte channel is separate from the diagnostic payload
catalog. Its packet carries the duplicate, original app request GUID,
PID/creation identity and measured file identity/hash, never a payload path.
The child uses the exact current native lease retained by its real
`FrontierAppLifetime`; copied leases cannot admit a packet. Packet scalars
alone do not create ownership. This is a trusted owned-process test workflow,
not a general untrusted handle-import API or a same-account security boundary.

The receiver takes responsibility for the duplicate before validating the
remaining binding. It checks the native file type, volume/file index and exact
length. A byte-identical copy at another native file identity is rejected.
One admitted receive attempt stays spent, and only the exact original lease
can take its stream once. Copied owners cannot take or close the original.
Observed invalidity retires the owner rather than permitting later revival.

The Service's real stream factory consumes that imported stream. A read-only
test wrapper counts actual async reads and checks the app lease before each
read; it does not supply the hash or call the metadata implementation itself.
The unchanged Service performs its normal accepted-source read and hashing.
The test waits for stream disposal and compares the real snapshot hash with
the measurement carried by the protected native handoff.

All receiver paths dispose their imported handles. The GUI source remains
held independently, and original app/attempt cleanup remains in force. If
delivery is interrupted before local adoption, the owned child's exit bounds
the remote duplicate; the sender never closes a possibly reused remote handle
number after publication. Partial-delivery/process-kill faults were not forced
in these focused cases.

The completion event only schedules fixture cleanup. Native normal exit,
writer closure, original output EOF and held source checks still run; neither
that event nor the app's diagnostic stdout marker is an acceptance capability.

## Verification

The first build found a test-side LINQ overload error. After correcting it,
the Windows PowerShell positive passed; the PowerShell 7 positive stopped
before child launch because Add-Type treated an uninitialized test fault field
as an error. Explicit initialization fixed that profile-loading issue. The
receiver's invalidation was also made one-way before final verification.
Neither failure was a live-network or runtime-product failure.

All 12 final focused cases pass on Windows PowerShell 5.1 and PowerShell 7:

- Full real Service metadata read of the held 256 MiB file.
- Wrong app request, wrong process creation identity, byte-identical foreign
  native file, and closed imported handle refusals.
- Default-loader isolation on each host.

The cases also check copied leases/source owners/requests, wrong role,
duplicate delivery, duplicate receive/take, native cleanup and unchanged
source/authority flags. Existing source checks still deny writes/renames
before and after child shutdown and permit exclusive access after retirement.

Final focused output is
`artifacts/frontier-attempt-sender-read-20260926/results/focused-final.trx`.
The initial positive run is development history, not part of the final count.
The single compatibility run passes all six prior cases: source-hold positives,
source/attempt-disposal controls and original Core-writer positives on both
hosts. `compatibility.trx` records that run. There are 18 distinct final passing
tests, zero failures and zero skips. No code changed during or after the two
final runs, and no aggregate lane was run.

PowerShell parsing on both hosts and whitespace checks pass. All 419 preceding
seal pins matched before editing. Five existing test/support files change;
the other 236 pre-existing dirty files remain unchanged. Four new test/support
files and this report bring the dirty count from 241 to 246. Of the preceding
pins, 415 remain unchanged; its four changed pins are the source partial,
loader and two GUI fixture hooks. The edited writer test helper was not among
that seal's pins and is protected by the fresh baseline comparison.

No production code, diagnostic catalog cap, coordinator authority, live
dispatch, acceptance rule, pre-verdict reservation, portable package, live
GUI/NKN run, full matrix, commit or push changed or ran. The incremental seal
is `artifacts/frontier-attempt-sender-read-20260926/FINAL-VERIFICATION.json`.

## Next edge

Extend the same native source identity into the actual payload-read operation,
including its separate read lifetime, without treating this metadata pass as
payload delivery. Then connect an app/transfer-owned finalized destination.
The receiver's current close-before-path-move finalization gap still needs
explicit design review before runtime changes. Continuous parent/analyzer
adoption and full-native selected-witness consumption remain later gates.
The strict second coordinator-published `file_tuna_v4` requirement is unchanged.
