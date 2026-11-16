# Protected original log: owned collector read

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-native-primary-files-20260925.md`.
Offline, test-only; production and acceptance remain unchanged.

## First missing edge and bounded change

The controlled GUI-writer fixture previously copied the producer log using
`ReadAllLines(GeneratedSource)` / `WriteAllLines`. The parent protected the real
producer file, but its native owner did not supply that collection read. The
original finalizer's output ownership did not establish this missing input link.

The opt-in fixture now sends only the original log handle from the actual held
file owner to the actual GUI request's bound child. It does not send the payload
files to the GUI. The parent checks original object identity, scope, live request
and the bound child's process ID/creation time, then duplicates into the actual
process handle. A separate one-use claim leaves the existing later three-file
primary handoff intact. The parent retains all three original handles throughout
collection; this is not a claim that the GUI becomes their sole owner.

The child checks the original GUI request's native channel, bound process,
scope, request ID and live parent. This read-only check does not consume the
finalizer attachment. It then validates the bounded stdin packet and the adopted
log's native identity and length. It copies at most 4 MiB through that handle,
using a 16-KiB buffer, into the collector's existing empty ledger. There is no
source-path reopen, content hash pass, line normalization or authority evaluation
in the copy. An already nonempty destination is rejected, not truncated.

The temporary source duplicate and destination stream close after copying. The
original ledger synchronization, finalizer, successful-child-exit requirement,
completion adoption and original primary analysis continue normally. The new
result is diagnostic-only; it is not a writer completion, retained-source token
or primary capability. Its exported JSON is never analyzer input.

## Verification

All **11 targeted tests passed**, with no failures or skips:

- `artifacts/native-collector-read-20260925/results/native-collector-focused.trx`:
  five integrations: Windows PowerShell and PowerShell 7 positive cases, plus
  foreign-request, wrong-file-identity and truncated-packet rejections.
- `artifacts/native-collector-read-20260925/results/native-collector-contract.trx`:
  six regressions, run once after stabilization: the existing GUI parent/finalizer
  fixture on both hosts (25 cases each), existing native-primary handoff on both
  hosts, and ordinary source-only primary on both hosts.

Every new handoff rejects a copied GUI request, copied file owner, unbound target
and repeated send. Positive children also reject a repeated collection read before
accessing stdin. Independent post-run assertions corroborate native source
identity, byte count and exact source/copy bytes; these checks grant no authority.
Each malformed case exits the actual child with code 1, leaves its ledger empty,
produces no finalizer completion and starts no primary analysis. The parent
retains its original files until normal disposal; post-run write-open checks
confirm file handles are released.

The positive Windows PowerShell attempt is
`ced89389-bfd7-4956-858e-56d8e1de176e` (166,166 copied bytes, 93 full / 16 compact
records, 88 ms diagnostic work). The PowerShell 7 attempt is
`2b99e27e-f682-49cb-8a1a-88146134feb3` (164,650 copied bytes, 92 full / 16 compact
records, 78 ms). Both retain exactly three original authority evaluations, both
genuine terminal rows, unchanged primary result views and the existing 20-second
diagnostic budget. Fixture log lengths vary; no exact shared record count is
assumed across independent transfers.

`GuiCompleted=true`, `GuiIntegrityOk=false`, primary
`FAIL_PROTOCOL_OR_INTEGRITY` / `fail`, and `TerminalIntegrityGate=unproven` remain.
The existing native-primary and parent-request custody controls still pass.

## Honest boundary and next step

The new child diagnostic reports
`Status=verified_offline_native_collector_read_only`. The primary's first missing
edge remains `file_writer_provenance_unproven`; this milestone does not advance
the acceptance gate. `WriterCopyProvenanceProven=false`,
`WriterMembershipVerified=false`, and `PrimaryFileIntegrityJoinProven=false`.

Specifically, the collected ledger is closed after the copy and subsequently
reopened by the existing path-based synchronization/finalizer lifecycle. We have
not preserved an owned native identity/stream relationship from that output into
the original finalizer capture and onward to the primary. Matching paths, hashes,
request IDs or post-run JSON cannot substitute for this connection.

Next: retain the actual collector output's ownership into the original finalizer
read/capture, then carry that owned association through its existing successful
completion and primary handoff. Keep the finalizer the sole issuer; do not take
its attachment early or keep the GUI alive past the successful-exit boundary.
Do not invent a failure witness, reread a one-shot witness source, or add another
authority evaluation. Writer membership and the complete canonical live-256
scenario remain separate obligations.

Collector-specific forced parent/child death, cancellation during copying,
post-copy replacement and nonempty-target fault injection were not separately
tested. Existing generic GUI cancellation/timeout regressions passed, but do not
prove these new-path cases. No full writer-provenance, security-boundary or live
readiness claim is made.

## Preservation

All 1,910 prior pins matched before work. Six previously dirty test files changed;
all 114 unrelated baseline dirty files are byte-for-byte preserved. Two previously
clean shared test fixtures changed, two test files and this document were added.
Of the prior pins, 1,902 are unchanged; the expected eight changes are seven
pinned test sources and the rebuilt test DLL. Production sources, loaders,
codecs, historical artifacts and the preceding verification seal are unchanged.

PowerShell parsing, `git diff --check` and touched-file whitespace checks pass.
The new seal is
`artifacts/native-collector-read-20260925/FINAL-VERIFICATION.json`.
No aggregate suite, portable rebuild, real GUI/network run, full GUI matrix,
commit or push was performed.
