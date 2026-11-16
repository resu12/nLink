# Windows destination lifetime implementation

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the first bounded step in
`file-transfer-destination-finalization-review-20260926.md`.

## Outcome

Guard-created Windows destinations now rename the actual open temporary file,
then retain a private read-only native handle before closing the writer. There
is no close/reopen-by-path interval on this path. All 17 new focused tests pass.

The existing compatibility run is **not fully green**: 15/16 passed, with one
sender-completion timing assertion failing. That test passed alone, but this
does not erase the mixed-run failure. Its unchanged path and the next required
investigation are described below. No live or full-matrix readiness is claimed.

## Implementation boundary

`SessionFileTransferGuard.cs` selects the new partial
`FileTransferWriteHandle.Windows.cs` implementation only for its own Windows
creation path. It preserves create-new, exclusive sharing, buffer size,
asynchronous/random-access I/O, authorization, storage roots, safe filenames,
collision numbering, and explicit overwrite policy. The native initial open
also requests DELETE access so it can rename that same open file. Unsupported
or denied native operations fail explicitly, with no pathname fallback.

The guard's finalizer flushes the original stream, renames by handle, duplicates
only read access, then closes the writer. The retained handle has neither write
nor delete access and stays private until ordinary handle disposal. There is
no public retention getter, app/transfer binding, external adoption, or new
acceptance/route capability. Normal Service terminal cleanup releases the hold;
the app-runtime round-trip compatibility test passed with that lifecycle.

Per-handle serialization prevents native finalization and sync/async disposal
from racing. Repeated successful finalization is idempotent; failed admitted
finalization cannot be reissued. Disposed native owners cannot finalize again.
Cancellation before admission does not create a retained result.

Physical rename and successful finalization are tracked separately. Failures
after rename preserve the final file, release native resources, and do not
report successful finalization. A failed rename preserves the temporary
artifact. Disposal of an unfinished owned file marks that native file for
deletion before closing it, rather than deleting a pathname after closure.
If a caller externally closed the writer, cleanup logs the inability to clean
the original and does not delete a possibly reused temporary pathname.

The public external-stream constructor retains its legacy behavior and cannot
acquire native retention merely by construction. The non-Windows branch is
unchanged; it was not executed on this Windows host. No coordinator, transfer
protocol, hashing policy, diagnostic budget, source-channel, or acceptance rule
was modified. Extended local paths and Unicode names are tested; SMB/UNC and
other filesystem implementations were not exercised.

## Focused verification

The new regression first failed against the original code because no native
handle remained after finalization (`red.trx`). The first implementation passed
rename/identity checks but its test reader failed: `RandomAccess.Read` attempted
to bind an async duplicate already associated with the original I/O completion
port (`green-positive.trx`). The reader was corrected to use a read-only native
mapped view, without reopening a path, rebinding I/O, or seeking a shared file
position. This is a test-reader correction, not a runtime retry/fallback.

`positive-mapped.trx` passed, then `focused.trx` passed all 17 cases:

- Full native identity/length/byte comparison across rename, with real async
  out-of-order writes of 2 MiB + 23 bytes; ordinary and long Unicode paths.
- Writer remains open at the post-rename checkpoint; only the read-only native
  duplicate remains afterward. Writable mapping and delete disposition are
  denied; pathname write/move/delete are denied until disposal.
- Late file/directory collisions, explicit overwrite, and preserved artifacts.
- Cancellation and sync/async unfinished-file cleanup, including repeat disposal.
- Closed writer plus a reused temporary pathname: the replacement is untouched.
- Post-rename exception, cancellation, and closed-writer faults: no retained
  success; final file and a deliberately reused temporary pathname survive.
- Concurrent finalization runs once; sync/async disposal waits for it.
- Legacy public constructor compatibility without native retention.
- A real sender/receiver Service transfer through the actual guard destination,
  verifying received bytes, final path, and native-hold release.

The scheduling/fault checkpoint is internal, per-instance, and null in ordinary
operation. It supplies no ownership, route, or acceptance authority. Tests keep
their file artifacts under the workspace; no user receive directory was used.

## Compatibility caveat and next investigation

`compatibility.trx`: 15 passing, one failing, zero skipped. This included the
entire 11-case security-guard contract class once, existing finalization-order
tests, stale bootstrap-finalization rejection, app authorization denial, and an
actual DevLocal app-runtime round trip. The runtime receive root was scoped to
the workspace via the existing test override and restored afterward.

Failure: `Receiver_StaysVerifyingUntilFinalizeSucceeds`, line 267, observed the
**sender** already `Completed` before releasing its test destination's finalizer.
Its receiver-not-completed assertion immediately before that passed. One
isolated diagnostic rerun passed (`compatibility-finalization-isolated.trx`).
Do not call that a clean compatibility rerun or remove/weaken the assertion.

This test uses the unchanged `CreateTempReceiveDestination` helper, not the new
guard-owned Windows path. Source inspection provides a concrete timing path:

- `SessionFileTransferService.PullTransferSessionV4.cs:24156` sends terminal-ready
  state after final chunk commit; `:24192` then begins hashing/finalization.
- `:11939`/`:11943` permit sparse-sender completion inference when terminal-ready,
  byte/chunk acknowledgement, and drained in-flight conditions hold.
- `:10874` selects that completion and `:11076` transitions the sender to
  `Completed` without waiting for this destination finalizer to return.
- The existing test waits for a transient file/state predicate before its
  separate assertions, rather than synchronizing on finalizer entry; scheduling
  can therefore expose or hide the earlier completion.

These files and the test/helper are unchanged in this implementation. The
mixed-run failure plus these current-code branches identify a completion-contract
inconsistency to investigate, not a native-rename failure. A pre-change binary
reproduction was not run, so this is not a measured before/after timing claim.

Next: make a deterministic reproduction of sender completion while receiver
finalization is held, review the intended meaning of `TerminalReady` versus
verified completion, and prepare the smallest fix before receiver-proof
integration. Do not silently accept early completion, alter the assertion to
pass, or expand this storage patch into coordinator/protocol changes.

## State and remaining scope

The preceding review's 49 pins matched before implementation. Only the guard
was edited among those pins; its other 48 pins remain unchanged. All 249
pre-existing dirty files are preserved. This step adds a tracked guard edit,
one native partial, one focused test file, and this report to the dirty set.
Branch/HEAD remain unchanged. The verification seal is
`artifacts/windows-destination-lifetime-20260926/FINAL-VERIFICATION.json`.

There is still no original-helper native receive proof, app/transfer-owned
destination evidence handoff, full 256 MiB destination verification, or parent
adoption. The retained private handle is only the storage-lifetime building
block. No portable build, GUI/NKN run, full matrix, commit, or push occurred.
The required second coordinator-published `file_tuna_v4` route is unchanged.
