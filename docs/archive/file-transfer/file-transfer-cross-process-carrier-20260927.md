# Offline cross-process NKN carrier — 2026-09-27

Status: focused verification complete. This is a 64 KiB carrier prerequisite,
not the retained 256 MiB source/destination/witness integration or live acceptance.

Baseline: branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, 372 dirty entries. All 831 pins in
`artifacts/payload-runtime-reads-20260927/FINAL-VERIFICATION.json` matched before
work; seal SHA-256
`0e141073f80d3b9d30aa5a94a36f20338827b90bb4350c1509dea0246a2a1985`.

## Missing connection and bounded implementation

The original cross-process fixture uses DevLocal and supports Regular v4 only.
The previous production NKN fallback fixture places both endpoints in one app
process. A test-only `AttemptNknPipeClient` now carries ordinary NKN envelopes
and Tuna bytes across a duplex named pipe between the original app processes.

The pipe is current-user-only and its endpoint is checked against the retained
native peer-process handle before binding and before/after physical I/O. Peer
identity still uses the original request, process creation time and held handle;
there is no PID-based reopen or replacement peer lookup. Direct destinations
are restricted to the retained peer's control/media/bulk addresses. Frame kind,
lane and length are bounded; a partial write closes the carrier rather than
reusing an ambiguous frame boundary. Topic-based discovery is unsupported.

The existing fake Tuna listener/dialer provides its physical lane over this
carrier. Production `NknSignalingTransport`, `SessionRuntime` and app-owned
`SessionFileTransferService` perform invite approval, protocol negotiation,
coordinator publication and correlated fallback. The pressure adapter is never
connected. No Internet/NKN bootstrap or real Tuna sidecar is launched.

Each endpoint independently checks current coordinator publications:
Regular v4, Tuna v4, then fallback v6. Tuna/fallback require applied transition
dispositions, and fallback additionally requires its completed bootstrap
exchange and source-hold receipt. Named events coordinate fixture timing only;
they neither publish a route nor bypass local coordinator checks. Payload reads
remain gated until both endpoints have independently reached fallback.

The receiver uses the runtime's guard-owned native destination. The test obtains
evidence at the existing rename/finalization boundary, rejects a premature read,
waits for actual finalization, checks the held file identity, hashes the one-shot
native reader, verifies EOF and rejects reuse. Both endpoints must complete on
fallback v6 with identical bytes, hash, session and transfer identifiers.

This lane deliberately transfers a generated **64 KiB** payload. The original
GUI fixture still retains its separate 256 MiB source without reading it through
this carrier. Its inherited scenario name is not proof of full-size transfer.
The small result cannot be transplanted into that source's payload pair or used
as its witness. It isolates the new physical connection before native source
integration, without changing existing deadlines or analysis work budgets.

## First failed boundary

The first actual carrier run reached initial Regular route publication, then
the new fixture asserted that the bootstrap agreement was enabled too early.
Initial publication precedes offer acceptance and runtime startup. The fixture
now waits for the actual enabled agreement and sender payload-reader opening /
receiver manifest boundary before requesting activation. This changes only
test ordering, not production authority or acceptance.

The initial test command used unsupported parameter-name filtering and selected
zero cases; it is not counted as verification. A later CS0136 local-name conflict
in an added file-identity assertion was corrected before test execution.

## Verification

Eight distinct final tests passed with zero failed or skipped cases:

- `carrier-focused.trx`: six tests, covering valid original process trees under
  PowerShell 7 and Windows PowerShell, wrong-request/wrong-pipe rejection under
  PowerShell 7, and default-loader isolation under both hosts.
- `legacy-cross-process.trx`: the existing 256 MiB DevLocal transfer and its
  wrong-request control still pass under PowerShell 7. This is compatibility
  evidence, not a full-size fallback result.

Results are retained in
`artifacts/payload-cross-process-carrier-20260927/results`. The first actual
carrier failure and one intermediate passing execution are kept separately;
neither is counted again in the final eight. The zero-case command and compile
error are not passes.

Both final carrier transfers produced 65,536 bytes with SHA-256
`4b640d85ab3ba30fd02c9fc9db4a8928f416322ad27022ea58a65aaee68a4df2`.
Each run's original sender and receiver report the same session and transfer
identifiers. Final positive process trees:

- `pwsh-valid-1f429b0ffb3e4b82bf49487b883aa210`
- `powershell-valid-88c995b966764d1da2c21cc2cd7a5d01`

The changed PowerShell seam parses with zero errors; scoped whitespace checks
pass. Of 372 baseline dirty entries, 370 remain byte-identical and two have
scoped fixture edits. The carrier, its test file and this report add three new
entries, for 375 total. Production code and all unrelated work are preserved.
The final hashes, inventory and test history are recorded in
`artifacts/payload-cross-process-carrier-20260927/FINAL-VERIFICATION.json`.

## Next boundary

Attach the previously verified retained-source runtime reader factory to this
cross-process production-route path and prove the actual 256 MiB destination.
Then join the original native source/destination pair through the existing GUI
and primary custody, preserving a genuine same-transfer lifecycle receipt and
payload marker. Do not synthesize an eligible failure or reuse this small
transfer's identifiers. The second Tuna publication remains unproven.

No production code, default-loader implementation, native work budget,
acceptance rule, live dispatch, portable package, commit or push is changed.
