# Original helper to GUI: native destination adoption

2026-09-26, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-buffered-ledger-validation-20260926.md`.

## Outcome and scope

The first remaining handoff is implemented: the original helper can offer its
completed, still-held read-only destination to the original GUI host. Both
offline 256 MiB cases pass with independently hashed GUI custody and source /
destination association to the same original closed-writer join.

The adopter is in the explicit test-only attempt profile. The Core exporter is
internal and opt-in; ordinary downloads and live GUI dispatch do not call it.
This is not GUI-to-wrapper-to-parent adoption, selected analyzer consumption,
live Tuna evidence, or a change to acceptance. No old artifact is promoted.

## Bounded implementation

`SessionFileTransferService.NativeDestinationGuiHandoff.cs` extends the existing
original evidence owner. It requires the exact Service, destination, and app
lease, completed native custody, and an unused export attempt. A copied or
foreign caller cannot consume the original attempt. An invalid original offer
retires rather than reissuing its custody.

The exporter checks the actual named-pipe server PID against the original
lease's still-current parent process identity. The GUI checks the actual client
against its existing original helper launch and native process handle. Endpoint
names and serialized IDs are locators/correlation, not authority. There is no
PID-based process reopen or pathname acquisition in this handoff.

The original read-only file is kept alive while a bounded offer carries its
native handle number, measured identity/length, completed session/transfer/hash,
request/process incarnation, and a fresh nonce. The GUI duplicates the file
through its already-owned helper process handle, with read-only access. It
checks native identity/length and hashes all bytes before acknowledging that
nonce. Core rechecks original custody and the actual pipe peer before returning
the commit nonce. Published numeric handles are never remotely closed by number.
They are not durable capabilities or saved receipts.

The Core exchange has a linked cancellation token and a ten-second maximum.
The GUI fixture has a bounded 75-second connection/adoption wait; the existing
sender/receiver wait uses its remaining interval after adoption rather than
starting another full wait. Neither the cumulative nor local five-second
ledger-processing budget changes. No diagnostic payload cap is enlarged.

The GUI's provisional owner retains a read-only duplicate through helper
shutdown. Its currentness checks require the original attempt, native file,
and original live launch or successful app-exit/output gates. A later writer
binding requires the exact original role-writer join. Disposing a copied
candidate does not close the original, and retirement cannot revive it.
The new `LiveProofEligible`, `TransferIntegrityEstablished`, and
`PublicationCompletionIssued` projections always remain false.

The offer/acknowledgment/commit exchange is not an atomic cross-process verdict.
Normal exit and original writer gates remain necessary after provisional
adoption. No app completion, saved-path publication, route, retry, progress,
fallback, or liveness state is changed by this evidence-only API.

## Verification and development findings

Results are in `artifacts/destination-gui-handoff-20260926/results/`.

- `gui-adoption-focused-final.trx`: 14 pass, zero failures/skips. Both PowerShell
  hosts cover valid adoption, wrong acknowledgment, mismatched file identity,
  peer interruption before acknowledgment, canceled export token, closed native
  hold, and a pipe served by the wrong process. Positives also reject copied
  Service/destination/lease/evidence/GUI owner, wrong role/request, repeated
  export/adoption/join, and copied-writer binding. Cleanup permits exclusive
  access only after retained owners have been released.
- `gui-adoption-full-size-final.trx`: both 268435456-byte original-process
  positives pass. The original sender, original receiver, and original GUI
  validate the held data, with original source and destination custody current
  together at the same writer join before deliberate test cleanup.
- `gui-adoption-compatibility.trx`: 17 pass, zero failures/skips. This is one
  selected lane: eight existing destination-lifetime cases, ordinary Service
  completion/file availability, two original writer positives, two original
  sender-read positives, two default-loader isolation cases, and two rerun
  GUI positives covering the final shared join helper. The two rerun positives
  overlap the focused lane. Across final applicable runs there are **31
  distinct passing tests**, not 33. No aggregate suite was repeated.

The small fixture transfers 2 MiB + 23 bytes through a real guard-owned receiver
and a same-child loopback sender. Only the two full-size cases connect the
original helpee and helper through DevLocal and SessionRuntime. Neither fixture
uses live NKN, paid transport, GUI clicks, or a rebuilt portable package.

Development artifacts are retained and are not claimed as passes:

- The initial build needed an explicit file-reading lambda in the test.
  `gui-adoption-initial.trx` then failed at Add-Type compilation of a test-only
  lock target. Both were fixture compilation issues.
- `gui-adoption-positive.trx` passed the first PowerShell 7 positive.
  `gui-adoption-focused.trx` passed seven PowerShell 7 cases and failed all
  seven Windows PowerShell cases because .NET Framework lacks the chosen
  safe-handle mapping overload. The final adopter instead uses an unnamed
  read-only native mapping and a 64 KiB managed hash buffer, with cancellation
  and owner/file checks during traversal. It never creates a full-payload byte
  array or reopens a pathname. The native API requirements are documented by
  Microsoft in [CreateFileMappingW](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw)
  and [MapViewOfFile](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-mapviewoffile).
- `gui-adoption-full-size.trx` failed both cases at the newly added destination
  writer join, after successful transfer/adoption. The existing source fixture
  intentionally disposes the shared writer join to test invalidation. The new
  check had been placed after that retirement. The bounded fix moves destination
  joining into the interval where the original source and writer join are still
  current, before the existing source retirement checks. It does not recreate,
  reopen, extend, or weaken the shared one-shot join. Both full-size reruns pass.

No forced parent/child kill or dispose-during-mapped-copy fault was injected in
this step. Existing original process-exit and ownership guards remain in place;
these tests do not establish a same-account adversarial security boundary or
complete downstream abnormal-exit relay coverage.

## Full-size artifacts

Under `artifacts/destination-gui-handoff-20260926/`:

- `pwsh-full-size-6d2dd45278d74dbfa8543a9fcce864db/`
- `powershell-full-size-b7c20c822b5f49b8933812f4eafddf6b/`

Both have empty GUI-worker stderr and all four required markers:
`attempt-destination-gui-adoption-ok`, `attempt-destination-closed-writers-ok`,
`attempt-source-custody-ok`, and `attempt-role-writers-ok`. Both original data
processes report SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`
for 268435456 bytes. GUI hash verification is an assertion over the adopted
native handle, not a reconstruction from these stdout markers.

## Next gate

Keep both original payload owners current while carrying their custody from
GUI through wrapper to the original parent. Use one-shot provisional adoption,
acknowledgment/commit, normal-exit checks, and retirement on cancellation or
abnormal exit at each hop. Bind the eventual original analyzer consumption to
that same attempt and writer provenance. Do not use the diagnostic payload
catalog, saved hashes, copied objects, or reopened paths as substitutes.

The live dispatch guard and pre-verdict selected-failure reservation remain.
The eventual route-cycle contract still requires a second coordinator-published
`file_tuna_v4`; SHA success and fallback completion cannot replace that proof.
Live testing is not the next gate. No commit, push, full matrix, or live run was
performed. The final recovery checkpoint is
`artifacts/destination-gui-handoff-20260926/FINAL-VERIFICATION.json`.

## Preservation

All 1343 previous checkpoint pins matched before implementation. Of the 288
pre-existing dirty files, 278 remain byte-for-byte unchanged; the ten intentional
edits are the original evidence type's partial declaration and directly affected
test/dispatch/source-join support. Five new files (the exporter, adopter, tests,
shared join helper, and this report) bring the dirty inventory to 293. Existing
artifacts, development failures, branch, and HEAD are preserved. Whitespace
checks and parsing of all six edited/new PowerShell scripts pass. The new seal
chains to the preceding buffered-ledger checkpoint and records the current
source inventory, complete test outcomes, and newly generated evidence.
