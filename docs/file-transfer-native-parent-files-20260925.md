# Native file adoption: producer to original test parent

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-held-terminal-files-20260925.md`.
Test-only first native hop; not an original-primary integrity handoff.

## Implemented and demonstrated

The genuine private C# held-file owner duplicates its three read-only native
handles into the actual PowerShell parent process that it just launched. A
fixed, bounded binary packet travels over that process's owned stdin pipe;
there is no path-nominated JSON input, named shared endpoint or production
receipt/codec change. The packet contains request/scope, target process ID and
creation time, and the original log/source/destination descriptors. The limit
is 16 KiB, exactly three files, a log at most 4 MiB and two 262,144-byte payload
files. This is the offline fixture's scope, not a new live-256 limit.

The PowerShell parent verifies the target process, request/scope, native disk
file identities and lengths, and takes local responsibility for the handles.
It acknowledges adoption once. The C# producer then disposes its original
handles and verifies that writes, rename and replacement are still denied:
at that point only the actual parent's duplicate handles protect the files.
After the producer's release acknowledgement, that same parent invokes the
unchanged original writer/source/primary fixture. Its native handles remain
held until the original primary has exited and its checks have finished.

The parent then disposes its handles; the original C# fixture checks successful
owned process exit, handle release and the genuine source/terminal binding.
Post-run file identities, sizes and independently measured hashes match the
parent's diagnostic descriptors. Repeated publication and adoption are rejected.
None of this adds an authority evaluation or modifies an original primary result.

The receiver deliberately labels hashes as **producer measurements**. It does
not run a new whole-file hash pass inside primary analysis or turn metadata into
file-integrity authority. Its output is
`nlink.test-native-parent-files/v1`, status
`verified_offline_native_parent_adoption_only`, with
`PrimaryFileOwnerHandoffProven=false` and `AcceptanceChanged=false`.

## Changes and verification

Added the native transport/receiver and wrapper under test `Support/`, plus
`FileTransferTerminalIntegrityEvidenceTests.NativeParent.cs`. The existing
held-file class became partial; the source fixture gained three native cases;
the primary fixture gained an opt-in native-parent launch branch. Existing
production, analyzer, writer, source-request and observer scripts are unchanged.

All **eight final targeted tests passed**, without failures or skips:

- `artifacts/native-parent-files-20260925/results/native-parent-controls.trx`:
  one test containing five real child-process rejection cases.
- `artifacts/native-parent-files-20260925/results/native-parent-contract.trx`:
  both native-parent hosts, both original non-handoff primary hosts and all
  three held-file lifetime/mutation/replay regressions.

Controls reject a foreign request, foreign scope, wrong process creation time,
wrong actual file handle and truncated packet. Each child exits unsuccessfully,
no adoption acknowledgement or success result is accepted, no writer collection
or primary analysis starts, and all duplicates are released by cleanup/process
exit. The genuine producer remains valid until its own disposal. The controls
are at `artifacts/terminal-source-tokens-20260925/0a2e7270-3f13-40f5-8aae-2d97303a73c8/`.

Accepted native captures under `artifacts/terminal-source-tokens-20260925/`:

| Host | Attempt | Original primary evaluations | Custody controls | Diagnostic work |
| --- | --- | --- | --- | --- |
| Windows PowerShell 5.1 | `b3772155-29a6-4837-b35e-66acc8fd94e4` | 3 | 6 | 93 ms |
| PowerShell 7 | `60cda656-5350-417e-9087-9aa94707dd68` | 3 | 6 | 66 ms |

Both retain 93 full / 16 compact records and the exact original terminal rows.
The source-only primary status, `GuiCompleted=true`, `GuiIntegrityOk=false`,
`FAIL_PROTOCOL_OR_INTEGRITY` / `fail`, unproven terminal-integrity gate and
unverified writer membership remain unchanged. The two non-handoff regressions
also preserve those outcomes and emit no native-parent result.

Development receipt `native-parent-focused.trx` is retained separately. Its two
positive hosts passed; the truncated-packet control exposed a test-harness
disposal-order bug (closing stdin before the binary writer disposed/flushed).
The writer now disposes before stdin closes. The affected control was rerun,
then the seven-case relevant regression lane ran once. No assertion was weakened.

## Limits and next boundary

This establishes the first native hop from the actual test producer into its
actual PowerShell parent, including protection after the producer's original
handles are retired. The producer process itself stays alive: producer death,
revocation during adoption and a production GUI-owned file issuer are not proven
by these tests. The helper is not a production security/authority API.

The adopted owner is **not yet bound to the original retained-source request or
delivered into original-primary custody**. Parent ownership and post-run JSON
assertions cannot substitute for that join. Next is that request-bound native
handoff, with original writer/source descriptors, completed full/compact
invocations and writer membership. The current primary first missing edge
therefore remains `file_owner_handoff_unavailable`.

Canonical scope, the complete five-route cycle and the closed independent-failure
inventory remain separate obligations. Do not combine these Regular-v4 attempts
with earlier Tuna-pair fixtures or reinterpret existing publication/analysis
receipts. This milestone does not make a live-256 run ready or adopt alternative
acceptance.

All 104 unrelated baseline dirty files are byte-for-byte preserved. Three
existing test partials changed; three test files and this document were added.
Of 598 prior pins, 594 remain unchanged; expected changes are those three test
partials and the rebuilt test DLL. Historical evidence and production files
are unchanged. `git diff --check` and touched-file whitespace checks pass.
The seal is `artifacts/native-parent-files-20260925/FINAL-VERIFICATION.json`.
No aggregate lane, portable build, live run, full GUI matrix, commit or push ran.
