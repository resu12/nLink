# Test-only held source to actual V4 payload read

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-attempt-sender-read-20260926.md`.

## Outcome and boundary

The original helpee child now uses the retained native 256 MiB source for both
the real Service metadata read and a separate real V4 payload-pump read. The
fixture receives one matching V4 chunk through an offline loopback peer and
then resets the Service. This is a bounded payload-read/delivery check, not a
complete transfer, receiver-file integrity result, or live route-cycle result.

The loopback peer is in the helpee child, not the original helper process. No
receiver Service or destination file is created. The existing helper startup,
writer closure, native process/output custody, and GUI source protection checks
still run. Parent analysis/reconciliation remain the earlier stand-ins; this
step does not establish terminal/selected-witness transfer association or
end-to-end native analyzer consumption.

## Change

The existing single-read channel gave its imported stream to metadata hashing;
Core disposed it before the payload pump could open its own source. The new
test-only `OpenNextRead` retains that original imported handle as an anchor and
uses `ReOpenFile` on it, never a pathname, for at most two non-overlapping reads.
Each new read is read-only, starts at offset zero, and must match the retained
native volume/file index and exact length. Closing either read does not close
the anchor. Disposing the original channel closes its anchor and current read.

Only the exact current app lease and original channel can open a read. Copied
owners/leases, overlapping reads, mixed use of the old one-shot API, and a third
read are rejected. Invalid native custody retires the channel without revival.
The old one-shot API now uses compare/exchange so a refused mixed call cannot
overwrite the paired-read mode. This is deliberately bounded fixture plumbing,
not a general retry/recovery factory or a new coordinator capability.

The real `SessionFileTransferService.CreateForApp()` receives the factory. Its
first call reads all 268435456 bytes through EOF and independently computes the
full hash. Its second call must follow disposal of the first read. A seekable
test wrapper records the first actual payload read, while the unchanged Core
pump controls chunk construction and dispatch. An ordinary offline V4 state
frame grants one chunk of credit; the peer receives the actual V4 batch and
checks the original session/transfer tuple, chunk index, size, and exact bytes
against that recorded read. No route fields, runtime owner, or pump installation
are assigned by the fixture. Any normal internal read-ahead is not counted as
additional delivered payload.

Reset waits for payload-stream disposal, verifies the anchor is still current,
rejects a third read, then retires the channel. No complete message/frame or V6
chunk batch is accepted as this result. The diagnostic stdout marker and GUI
completion event do not grant acceptance authority.

## Verification

Both initial positive cases passed. After tightening the negative assertions,
all six final focused cases passed: positive payload delivery, imported anchor
closed between reads, and explicit channel disposal between reads, on Windows
PowerShell 5.1 and PowerShell 7. Negative cases require the exact factory-side
rejection checks to finish as well as the actual Service failure; an unrelated
factory exception cannot satisfy the test. They emit no chunk batch.

The single existing metadata/channel compatibility run passed all twelve cases,
including default-loader isolation and request/creation/foreign-file/closed
handle refusals on both hosts. There are 18 distinct final passing tests, zero
failures and zero skips. No source changed during or after those final runs.
`focused-final.trx` and `compatibility.trx` record them. Final file pins are in
`artifacts/frontier-attempt-payload-read-20260926/FINAL-VERIFICATION.json`.
The initial two positives are development history, not additional distinct
tests. No aggregate suite was requested or run.

All 380 previous seal pins matched before editing. Only two pre-existing files
change; the other 244 dirty files are preserved. The new focused test and this
report bring the dirty count from 246 to 248. The previous seal retains 378
unchanged pins, with the two intentional source edits recorded in the new seal.

No production code, diagnostic catalog budget, coordinator authority, live
dispatch, acceptance rule, pre-verdict reservation, portable package, live
GUI/NKN run, full matrix, commit, or push changed or ran. Native GUI/network
actions remain stubbed by the existing explicitly selected offline fixture.
Interrupted handle delivery/process kill and active-read retirement races were
not fault-injected in this step.

## Next edge

Review the receiver destination's lifetime/finalization contract, then connect
the original helper's actual receive path to an app/transfer-owned destination.
Its current close-before-path-move finalization gap must not be replaced by a
later pathname/hash match as custody proof. Complete 256 MiB delivery, finalized
destination integrity, continuous parent/analyzer adoption, and native selected
witness consumption remain unproven. The strict second coordinator-published
`file_tuna_v4` route requirement remains unchanged; this is not live-256 readiness.
