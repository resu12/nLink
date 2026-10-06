# Live-256 terminal integrity and missing Tuna activation: RCA and fix plan

2026-09-30. Branch `v0.7.0`; reviewed HEAD
`77df4ccb539c0c269de5ca2f0ffeee38ef8ba036`.

Current status: milestone 1 is implemented and verified offline in the working
tree, and milestone 2 capture review is complete without new instrumentation.
The original live run is still failed; milestone 3 remains. Only
the GUI measurement/reporting harness changed in production. Acceptance,
timeouts, route policy and native file-sharing protections are unchanged.
No new live run, portable rebuild, commit or push was performed.

## Outcome

The latest targeted run remains **FAIL**. It contains two distinct failures:

1. **Confirmed hashing incompatibility:** the GUI terminal check reopens the
   received pathname while native destination custody can still retain an
   exclusive file hold. The production hash helper fails with a sharing
   violation. The underlying handle/hash incompatibility is reproduced offline.
2. **Independent route failure:** the initial Tuna precommit probe was reported
   sent, but matching peer receipt/ACK evidence is absent before the sidecar
   closed. Neither the first nor second Tuna route publication is proved.
   The cause of the stream closure remains unresolved.

Both transfer terminals report Completed. After process cleanup, the source and
destination are each 268,435,456 bytes and have identical SHA-256:
`8dc00e829350997adbed1ad4e87af5c2c56376242a3e920fcd3265612359cdc0`.
This is post-run diagnostic evidence, not a replacement for in-run integrity,
custody, route or acceptance proof.

## Evidence and freshness

The reviewed run is
[20260930T115636Z](<../artifacts/live-checkpoint-77df4ccb-20260930/20260930T115636Z>),
scenario `regular-v4-live-activation-off-on-off-256mb`, one attempt.
It used the freshly built portable from the reviewed clean HEAD with strict
preflights, outside the restricted sandbox. Connectivity, isolation and
provenance passed. The existing acceptance rules were not bypassed.

[Post-run review](<../artifacts/live-checkpoint-77df4ccb-20260930/20260930T115636Z/post-run-review.json>)
pins 15 original artifacts. All 15 size/hash pairs were rechecked during this
RCA and still match. The GUI harness and wrapper hashes also match the run's
manifest. Build verification SHA-256 remains
`09b8f4be304961e75d075343ddeab8f3657420e4f31c530731c92266ba24522d`.

### Terminal hashing root cause

The relevant sequence at the reviewed pre-fix HEAD was:

- [Native destination creation](<../src/nLink.Core/SessionSecurity/FileTransferWriteHandle.Windows.cs#L21>)
  opens the destination with sharing disabled.
- Finalization renames that same file and keeps a read-only duplicate, then
  closes the writer. Read-only rights do not remove the original sharing
  reservation while a duplicate remains alive.
- The native destination handoff can transfer that retained file into app/GUI
  custody. Ordinary download completion releases its remaining guard hold, but
  that does not release a hold already moved into native evidence custody.
- [GUI terminal hashing](<../tools/GuiSmoke-Windows.ps1>)
  calls [Get-FileSha256Hex](<../tools/GuiSmoke-Windows.ps1#L2556>),
  which uses `File.OpenRead(path)`. Destination adoption is joined later in
  cleanup, after this check; the background adoption can already hold the file.
- The exception occurs before terminal states, completion and integrity are
  assembled into the normal GUI summary.

The exact live process/handle retaining the file was not independently captured.
Its ownership is therefore a source-supported inference, not a proven live PID.
The incompatibility itself is confirmed using the production native creation/
finalization code and the actual production PowerShell hash function:

| Hash host | Retained read handle alive | Handle released |
| --- | --- | --- |
| Windows PowerShell 5.1.26100.9549 | IOException, Win32 error 32 | SHA matches |
| PowerShell 7.6.6 | IOException, Win32 error 32 | SHA matches |

[Diagnostic result](<../artifacts/terminal-integrity-rca-20260930/lock-probe-result.json>)
and [reproduction script](<../artifacts/terminal-integrity-rca-20260930/Invoke-LockProbe.ps1>)
record four expected outcomes on a new 60-byte local file. This exercised the
primitive boundary, not a full authenticated app/GUI handoff or a live transfer.
That initial RCA reproduction did not run an automated regression suite.
The subsequent implementation checks are recorded below.

Existing inspected custody tests cover source/attempt/helper association and
retirement. The missing regression should exercise that custody together with
the real terminal hashing consumer, not merely each component separately.

### First missing route proof edge

Times below are UTC; line numbers refer to
[the retained log](<../artifacts/live-checkpoint-77df4ccb-20260930/20260930T115636Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-retained-log-slice.log>).

| Time | Retained lines | Observed event |
| --- | --- | --- |
| 11:58:17–21 | 20299–20324, 20726–20737, 20977–20988 | Initial activation decisions say receive recovery is not blocking. |
| 11:58:22–23 | 21009–21011, 21035–21036 | Listener stream/bridge starts; initiator accepts the answer. |
| 11:58:23 | 21056–21086 | Answer-ACK barrier releases; coordinator admission and send-drain staging lead to probe launch. Responder is waiting for peer probe evidence. |
| 11:58:23 | 21091, 21094 | Precommit probe delivery reports sent over Tuna; transaction/offer/lease generations are 1/1/1. |
| 11:58:27 | 21809–21812 | Listener reports Tuna stream EOF, local IPC EOF and bridge terminal. |
| 11:58:27 | 21819–21821, 21831 | Probe fails with sidecar-closed reason, ACK timestamp zero and route uncommitted; dialer reports remote closed. |

No matching peer acceptance/handoff or probe ACK-delivery/sent record was found
in the retained log. The search included the event names emitted by the current
transport source, not only guessed receive-event names.

The first missing edge is **sent Tuna precommit probe → authenticated peer
receipt and matching ACK**, before route commit. A send result or forwarded
bridge frame is not proof of peer receipt. Likewise, the transaction's earlier
`peer_received=1` describes negotiation state, not successful probe receipt;
its probe ACK time remains zero.

The artifact does not establish whether transport loss, local receive/dispatch
behavior or lifecycle shutdown caused the EOF. The compact retained log omits
richer bridge terminal records that are present in the full capture, reviewed
under milestone 2 below. Those records still do not identify the exact probe's
receive/ACK boundary. Do not label this an external-network fault or patch
retry timing on the present evidence.

### Reporting consequences, not additional proven root causes

The retained terminal summary says inbound/outbound Completed and
`completed=1`, but `sha_ok=0`. The GUI error fallback summary says
`completed=false`, `integrityOk=false` and unknown terminal states because
the hash exception interrupted the normal summary path. This mismatch must not
be interpreted as proof of payload corruption.

The direct evidence manifest has `artifactFinalized=true`, passing cursor/
writer checks and a completed valid retained-analysis receipt, but
`currentRuntimeProof=false`. In
[Write-TunaGuiDirectEvidenceManifest](<../tools/Run-FileTransferTunaGuiSmoke.ps1#L2472>),
a zero GUI exit and a completed/integrity-valid summary are mandatory.
This run has GUI exit 1 and an invalid success summary. Therefore the
[attempt-finalization gate](<../tools/Run-FileTransferRouteAcceptance.ps1#L9709>)
rejects selection, producing the final selected-attempt-missing class.
Do not force selection or runtime proof to true.

The empty derived-input-receipt hash is not independently a failure: that
receipt is optional when the original analyzer-input hash matches the ledger;
artifact finalization already passed here.

Failure-classification enrichment timed out after approximately 30 seconds.
The separate retained analyzer completed with exit 0 in 536.062 seconds and did
not time out. Increasing either limit is not a supported fix for the lock or
the missing probe ACK.

## Bounded fix plan

### 1. Implemented terminal measurement with the held destination

Implemented scope: the terminal measurement helper/call site in
[GuiSmoke-Windows.ps1](<../tools/GuiSmoke-Windows.ps1#L2594>)
and a narrowly scoped read operation in
[LocalGuiWriterCanonicalDestination.cs](<../tools/FileTransferOps/LocalGuiWriterCanonicalDestination.cs#L86>),
plus directly affected tests. No Core/Infra route change is currently justified.

The implemented measurement contract is:

- Complete the already armed adoption within the existing scenario deadline;
  use only the original, current same-attempt destination owner.
- Measure actual bytes through its read-only native capability, with attempt,
  helper, session/transfer, source, file identity and length checks. Validate
  currentness before and after the read; cancellation or retirement fails
  closed. Reuse the existing read-only mapping technique where practical.
- Return only measured size/hash evidence. Do not copy the cached advertised
  `Sha256Base64` into a successful terminal result without reading the bytes.
  Keep `LiveProofEligible`, `TransferIntegrityEstablished` and
  `PublicationCompletionIssued` false on provisional custody.
- Keep the existing independent Completed/error checks and expected-source
  hash comparison. The read capability grants no route/runtime or publication
  authority and does not complete missing parent/analyzer adoption.
- Preserve the ordinary diagnostics-off pathname path. In native mode, absent
  or stale custody must not fall back to a path reopen or post-cleanup hash.
- Snapshot/report factual terminal states even if measurement fails. Report
  integrity unavailable/failed with its cause, preserve the incomplete route
  proof and nonzero result, and retain normal cleanup.

Do not widen file sharing, close custody early, retry until the path becomes
readable, add a new authority evaluator, or relax acceptance. If safe
measurement cannot fit the existing ownership lifecycle, stop at that specific
contract gap instead of inventing a second ownership system.

Verification before live use:

| Focused regression | Required result |
| --- | --- |
| Real terminal consumer with authenticated original destination still held, PS 5.1 and PS 7 | Complete size/hash measurement; a separate pathname open remains denied; custody remains alive. |
| Wrong file with equal bytes, foreign transfer/attempt/helper, stale or retired owner | Reject without path fallback, cached-hash success or authority effects. |
| Cancellation/disposal during read; adoption absent or failed | Bounded failure and cleanup; no successful integrity assertion. |
| Completed Regular transfer with no Tuna publication | Factual terminal/integrity results remain distinct from route FAIL. |
| Measurement exception after terminal completion | Original terminal facts and hash error both survive reporting; runtime-proof/selection gates stay fail-closed. |
| Diagnostics off | Existing pathname hash behavior and ordinary file release remain unchanged. |

Focused cases were added beside the existing GUI-harness and canonical-destination
tests. Verification used affected tests during edits and one relevant existing
contract regression set after stabilization; no aggregate suite was run.

### Offline implementation verification

The native measurement waits for the existing armed handoff within the caller's
remaining scenario budget, or uses the already adopted original destination.
It checks current custody and the exact terminal session/transfer evidence
tokens, hashes the mapped bytes, and checks currentness again. The returned
size/hash is measurement data only. Both terminal states remain in the GUI
summary if measurement fails, with a separate measurement error.

All **12 new focused cases** and **10 existing contract regressions** have
passing final results. The real-file cases use the existing offline
cross-process Core receiver fixture and its authenticated native destination;
they do not exercise NKN or the desktop GUI. Separate synthetic helper cases
exercise the armed handoff, rejection, cancellation and deadline integration.
PowerShell 5.1 and 7 both cover successful held-file measurement and ordinary
hashing/failure reporting. Mid-read cancellation/disposal, scope mismatch,
copied attempt, cached-digest substitution and an equal-byte wrong native file
are rejected or handled without granting authority or reopening the path.

Receipts:

- [Initial focused run](<../artifacts/terminal-integrity-fix-20260930/terminal-integrity-focused.trx>):
  5 passed, 1 failed because a test callback could not see a private PowerShell
  variable. A direct delegate to the original Dispose method corrected that
  fixture; the failure is not omitted from the retained receipt.
- [Focused follow-up](<../artifacts/terminal-integrity-fix-20260930/terminal-integrity-followup.trx>):
  9 passed, including the corrected disposal case, strengthened summary checks
  and six armed-handoff cases. Together with the unaffected initial cases,
  the latest result for each of the 12 focused cases is pass.
- [Existing contract lane](<../artifacts/terminal-integrity-fix-20260930/terminal-integrity-contract.trx>):
  10 passed, covering canonical source/helper association and retirement,
  normal loader exposure, summary validation, direct-manifest requirements
  and the unchanged strict route-cycle contract.

Earlier test-loader setup failures are retained in the same artifact directory;
they are not claimed as reproductions of the production defect. The offline
build disabled optional Avalonia telemetry because its log lies outside the
workspace sandbox. No application or acceptance setting was changed.

The final diff is limited to the two production harness files, focused tests
and this report. All 15 original live-run artifact pins still match. These
offline results close the hashing/reporting implementation milestone, not the
Tuna route failure or live-256 acceptance.

### 2. Completed probe capture review

No new instrumentation is needed before the next attempt. Existing
[bounded delivery tracing](<file-transfer-tuna-delivery-capture-plan.md>)
covers the unresolved boundary. No `tuna_delivery_trace` records were found in
the latest run's full operational log, full ledger or compact retained log.
The trace is separately opt-in; ordinary file-transfer tracing does not enable
it. Its absence cannot establish where the probe was lost or rejected.

The [full ledger](<../artifacts/live-checkpoint-77df4ccb-20260930/20260930T115636Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-gui-log-ledger-slice.log>)
contains two richer records at 11:58:27 UTC, omitted from the compact log:

| Full ledger line | Sidecar | Terminal observation |
| --- | --- | --- |
| 31280 | Listener | `tuna_stream_eof`, `tuna_to_app`, read stage; terminal-direction frames forwarded 0; aggregate bytes moved 545. |
| 31312 | Dialer | `tuna_stream_eof`, `tuna_to_app`, read stage; terminal-direction frames forwarded 1; aggregate bytes moved 0. |

These counters have different scopes and update points in `bridgeDirection`.
Neither count identifies a frame by its wire bytes; zero aggregate bytes does
not prove no frame was forwarded. The terminal's last lane is control, not an
authenticated probe identity. Both records leave the same first proof edge
unresolved. Full-ledger SHA-256 is
`326dd5bc35e8d782279984eeff7857ea6f14713d68976a5754d98dde3d2a6220`.

The existing trace supplies exact-wire hashes at queue acceptance, .NET IPC
write, both Go bridge read/write boundaries, receiver IPC read before the
availability guard, callback entry or unavailability rejection, application
raw ingress and the existing decoded-frame receive disposition. The transport
binds send results and deferred receive records to the original frame, rather
than a mutable sequence counter. Listener and dialer adapters preserve only
allowlisted fields under the captured process owner. Raw ingress without a
matching disposition still leaves an application boundary unresolved; the
trace does not manufacture an authentication result or ACK.

Before launching either app in the next targeted attempt, set
`NLINK_TUNA_DELIVERY_TRACE=1` and one fresh D-format GUID in
`NLINK_TUNA_DELIVERY_CAPTURE_ID`. Both app roles and sidecars inherit that same
capture ID. The .NET producer is fixed at process initialization, so toggling
the environment after startup is insufficient. Restore the prior environment
after the attempt. Keep the existing 64 bulk frames per direction/connection,
four connections, 4096 records and 4 MiB per-producer caps unchanged.

Retain and pin the full ledger and writer capture separately from compact
analyzer input. Join the precommit send's envelope fingerprint to its exact
wire hash, then inspect the first missing positive stage and any matching ACK.
Do not join by timestamps, byte counts or sequence alone. Cap markers, ambiguous
occurrences, missing output drain and `trace_tail=unproved` remain limitations;
absence of a record is not proof of nondelivery. Trace results do not enter the
primary acceptance predicates or grant coordinator authority.

Offline verification on the unchanged tracing implementation:

- [18 focused .NET cases passed](<../artifacts/tuna-probe-capture-review-20260930/tuna-delivery-trace-focused.trx>),
  covering exact-wire binding, deferred application receive, unavailable
  callbacks, default-off behavior, bounds, redaction, both stdout adapters and
  full retention despite compact omission. The filter was
  `FullyQualifiedName~TunaDeliveryTrace`; no aggregate suite was run.
- [Four Go trace tests passed](<../artifacts/tuna-probe-capture-review-20260930/go-delivery-trace-output.txt>),
  including five subtests for forwarding, write failure and incomplete output.
  Command: `go test -count=1 -run '^TestDeliveryTrace' -v .`, with workspace
  `GOCACHE` and `GOPROXY=off`. These tests use local pipes, not live NKN.

No tracing, Core/Infra, Go, acceptance or retry code changed in this milestone.
A runtime fix still requires a deterministic reproduction or fresh evidence
identifying the failed boundary; the present EOF alone is insufficient.

### 3. Rebuild and rerun the single targeted scenario

After the bounded implementation and relevant regressions pass, review the diff,
prepare the clean checkpoint required by provenance, and rebuild the portable.
Run only `regular-v4-live-activation-off-on-off-256mb` outside the restricted
sandbox with strict campaign/connectivity/isolation preflights. Do not bypass a
failed preflight. Preserve the failed run and use a new artifact directory.

Success requires usable in-run terminal integrity evidence **and** the unchanged
route-cycle contract, including a second coordinator-published
`file_tuna_v4`. Regular-v4 and Tuna-v4 remain protocol 4; post-Tuna fallback
remains protocol 6; active Tuna-v6 is invalid. Post-run SHA, sidecar readiness,
send acceptance and fallback completion cannot substitute for that proof.

If the new run exposes a new first proof edge, prepare its bounded RCA.
Do not proceed to the full GUI matrix while the targeted correctness path is
still unresolved. Performance remains a separate assessment.

## Recommended next action

Proceed with milestone 3: review/checkpoint the terminal-integrity changes,
rebuild and rerun the single targeted scenario with the existing bounded
delivery trace enabled before app startup and full-log retention preserved.
Do not treat the completed offline fix as approval of the old live run or as a
change to its acceptance contract.
