# Authenticated same-transfer producer and witness boundary

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-same-transfer-producer-20260926.md`.

## Outcome

The offline producer now uses the real file-transfer Services attached to an
approved FakeNkn pair, with the existing fake Tuna lane. The single-transfer
case obtains the original primary's witness and completion from that same
transfer's authenticated send. Real 256 KiB source and receive files complete
with matching hashes.

This is not a live Tuna test or a completed terminal/payload integration.
The analyzer still has no native payload owner; terminal consumption remains
disabled. The deliberately seeded mismatch still produces
`FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`. Acceptance is unchanged.

## Implementation

The existing preparation test lane gains a default-off forwarding callback.
The new fixture uses real public approval/preparation, then temporarily queues
the original encrypted sends (bounded to 4 MiB). Queue acceptance allows the
NKN send to finish and write its own security/publication record before any
receiver payload arrives. Existing tests retain their original delivery path.

The producer decrypts the captured original envelope using a copy of this
transport's installed key, immediately zeroes that key copy, and checks its
authenticated session, transfer and legacy-v4 frame. It verifies the original
send's security and envelope fingerprint fields using the existing production
capture functions. It does not encrypt a replacement, fabricate fingerprints,
or borrow another transfer's security context.

While holding the actual sending Service's gate, it checks the current
coordinator-issued Tuna-v4 publication and exact original log suffix. Before
releasing the queued bytes, it logs one explicitly labeled seeded observation,
changing only the reported transport fields. No records are appended after
logger drain, and no writer metadata is invented.

The private owner retains the actual envelope, publication evidence, run,
original/seeded source rows and both completed Service snapshot objects.
Copied owners, envelopes, publications, rows, swapped original/seeded rows and
copied terminal snapshots are rejected. A second actual authenticated transfer
with equal payload bytes also cannot supply the owner, relabeled owner, file
capture, envelope, publication, failure row or terminal rows: seven foreign
substitutions are rejected. JSON diagnostics are outputs, not authority.

## Resource boundary preserved

Two authenticated transfers in one original log exceed the native witness
adapter's unchanged reservation:

`8 * full records + 4 * compact records + 2 * writer records <= 4096`.

The final foreign case has 488 full and 64 compact records; those two terms
alone require 4160 visits. It therefore correctly omits the original witness
with `primary_witness_writer_unavailable`, issues no borrower, and disposes
all four adopted outputs. The test explicitly verifies this negative boundary.
The single-transfer case still requires the original positive witness; no
budget, missing-security rule, profile refusal or production condition changes.

Foreign-owner rejection is proved within the real producer. It is not claimed
as a successful two-transfer analyzer handoff. Such a positive remains a later,
separately bounded integration concern.

## Verification

Both new cases pass on Windows PowerShell 5.1 and PowerShell 7:

- Positive: `9cd4a4f8d249420cbb75f8838cac75f3`.
- Foreign-owner controls and budget omission:
  `fcb1b6893b974e37b9167abba6c77ca6`.

Both retain source line 102 as the primary's selected failure, corresponding
to original writer/producer line 143; the genuine original send is line 138.
The positive has 243 full, 32 compact and 328 writer records. It uses 2814
cumulative record visits, one original writer EOF pass, no extra writer reads
and exactly three original authority evaluations. The foreign case also
retains exactly three evaluations and the same failed route verdict.

Final focused results:
`artifacts/authenticated-transfer-20260926/results/authenticated-verified.trx`.
The affected compatibility run passed 15 of 16 cases. Its sole failure was
an existing asynchronous assertion in the prepared-Tuna payload test: peer
receipt can precede the sender's post-send `ChunksAcceptedForTransport` update
(`PullTransferSessionV4.cs:17750,18407`). The test now includes that counter in
its existing three-second wait, retaining the original assertion. All four
payer/sender combinations pass in `prepared-payload-synchronized.trx`; unrelated
lanes were not rerun. Together with the two new cases, all 18 distinct targeted
cases have passing evidence, with no skips. The earlier failing TRX is retained.

The build, both PowerShell parsers and diff whitespace checks pass.
Development failures remain recorded: the fixture initially used the default
decoder, which rejects legacy-v4 frames; the two-transfer case initially
expected a positive above the work budget; and its assertion mode initially
was not propagated to the PowerShell host. These were fixture issues, not
evidence of a production transport defect.

All 705 previous milestone pins matched before edits. The current previous-seal
differences are the two intentionally edited existing support/test files and
the rebuilt test DLL. Of 198 pre-existing dirty entries, 196 remain byte-for-byte
unchanged. The previously clean preparation test fixture has only the six-line
default-off seam change; the previously clean payload test gains the one-line
wait condition described above. Four new test/support files and this report
bring the dirty-entry count to 205.

No production code, acceptance, mixed-profile refusal or pre-verdict guard
changes. No portable build, live-256 run, GUI matrix, commit or push.
The incremental seal is
`artifacts/authenticated-transfer-20260926/FINAL-VERIFICATION.json`.

## Next bounded step

Integrate a test-only, post-report native payload handoff for this authenticated
single-transfer producer. Preserve its exact original witness/borrower,
collector origin and private transfer/file custody. Do not substitute these
diagnostic JSON files, reopen paths as ownership proof, or enable incompatible
profiles together. Add only the new work to the original clock and counters,
and keep foreign-owner and insufficient-budget cases fail-closed.

Continuous payload custody, the real two-GUI-writer parent, canonical live-256
five-route cycle and any pre-verdict ordering change remain separate gates.
