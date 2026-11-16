# Production handoff checkpoint review

Checkpoint approval is withheld because the destination listener can leave a
connected read pending after cancellation on Windows PowerShell 5.1. GUI cleanup
then has an unbounded wait on that task. Fix this cancellation edge before a
local checkpoint, portable rebuild, or targeted live run.

This review changes no production code, test fixture, acceptance rule, timeout,
or runtime policy. No build, live preflight, GUI/NKN run, commit, or push occurred.

## Scope and preserved state

The starting seal is
`artifacts/production-primary-payload-verification-20260930/FINAL-VERIFICATION.json`,
SHA256 `3aa4673516e6f8168292bb266180ff5985e750c0fe11d73cba00c0dcbad87c2e`.
All 700 dirty entries matched at entry. They comprise 223 documentation,
33 application-source, 389 test, and 55 tooling files. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with an empty index.

The review traces the recent production ownership chain, its cleanup and
authority boundaries, selected ordinary-transfer compatibility paths, and
packaging prerequisites. It is not exhaustive approval of all 700 files.
The earlier ten focused checks remain valid within their recorded scopes.

The previously missing production calls now exist: guarded scenario dispatch,
destination adoption, closed writer and payload offer, wrapper listener and
terminal take, parent pull and original export, and primary transfer binding.
The analyzer still performs optional diagnostics after writing its original
reports; the transfer association grants no route or publication authority.

## Blocking finding

Priority P1: make cancellation interrupt the connected destination pipe.

`LocalGuiWriterCanonicalDestination.cs:132` creates the linked deadline, but
does not register closure of that exact pipe when the deadline or the original
scenario token is cancelled. Its `Read` helper at line 278 synchronously waits
for `Stream.ReadAsync(..., token)`. Cancelling that token does not interrupt an
already-pending connected pipe read on the tested Windows PowerShell host.

`TryCompleteDestinationAdoption` calls cancellation at lines 64 and 65, then
unconditionally calls `pending.GetAwaiter().GetResult()` at line 66. Consequently,
the 75,000 ms wait is not an upper bound on cleanup. If the original helper
connects and then stalls without closing its pipe, this call can remain blocked
until the peer or an outer watchdog closes the connection/process. The scenario
invokes it before shutting down the apps, and attempt disposal also joins it.

This is a cancellation/lifetime defect, not evidence for increasing a timeout.
The wrapper already addresses the same connected-read behavior by registering
its linked token to dispose the exact pipe in
`LocalGuiWriterCanonicalPayloadWrapper.cs:215`.

## Offline reproduction and coverage limit

The new artifact-only probe loads the normal production native types and invokes
the unchanged `CanonicalAttemptDestination.Read` method on an actual connected
local asynchronous pipe. Its peer sends no header bytes. The probe waits until
the read is pending, cancels its token, observes it for 2,500 ms, and then closes
the exact server pipe for guaranteed cleanup.

| Host | Read completes after cancellation | Completes after pipe closure |
| --- | --- | --- |
| Windows PowerShell 5.1.26100.9549 | No, task remains Running | Yes |
| PowerShell 7.6.6 | Yes, task is Faulted | Yes |

Both probe processes exited successfully after cleanup. These are diagnostic
observations, not passing regressions for the faulty contract. The probe does
not exercise a full GUI hang, authenticate a receiver payload, or establish any
route evidence. The unbounded-cleanup consequence follows from the production
call path above and the reproduced read behavior.

Existing destination-lifetime tests cancel or dispose before the peer connects.
Their malformed-packet case sends a complete invalid length and closes the peer.
They therefore do not cover cancellation during a connected header/body/commit
stall. The preceding wrapper cancellation tests exercise a different listener.

## Bounded fix and verification plan

1. Add focused normal-loader regressions with an original helper that connects
   and stalls at the header, packet body, or commit boundary. Keep the peer open
   until the server ends the exchange so its own timeout cannot mask the defect.
   Cover explicit cancellation, scenario expiry, and attempt disposal on both
   PowerShell hosts. Require task completion, no adopted custody, and released
   resources before app shutdown.
2. Register the existing linked destination token to dispose that exact pipe
   instance. Scope and dispose the registration before cleanup takes ownership
   locks. The callback must only close I/O: no ownership evaluation, joins,
   coordinator calls, retries, or custody publication. Preserve connection
   timing, 75,000 ms GUI and ten-second receiver limits, and all original checks.
3. Run the new regressions, then the directly affected destination-lifetime and
   destination-adoption compatibility cases. Do not repeat the unrelated primary
   integration or aggregate suites unless their covered behavior changes.
4. Resume checkpoint review after those cases pass. Retain the current strict
   acceptance contract and do not count cancellation repair as live acceptance.

## Remaining checkpoint and build gates

The source-cleanliness gate still reports `source_dirty` in
`tools/FileTransferRouteAcceptanceEvidence.ps1:2054`. A hash seal over dirty work
does not satisfy it. Review the intended checkpoint without automatically
committing unrelated changes or weakening the provenance rule.

The portable builder recursively cleans its selected canonical output directory
at `installer/Build-Portable.ps1:482` and overwrites its selected ZIP. A later
build must use resolved, workspace-contained output paths that do not overlap
retained evidence. No packaging cleanup was invoked here.

After the fix and reviewed clean checkpoint: rebuild portable, pin runtime and
harness provenance, then perform strict escalated preflights for the targeted
`regular-v4-live-activation-off-on-off-256mb` attempt. The full matrix is not the
next gate. A second coordinator-published `file_tuna_v4` remains mandatory;
payload integrity, fallback completion, and diagnostic output cannot replace it.

The probe, observations, preservation audit, and final review seal are under
`artifacts/production-handoff-checkpoint-review-20260930`.
