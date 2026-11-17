# Held terminal files: offline lifetime prerequisite

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the test-only prerequisite in
`file-transfer-file-owner-handoff-contract-20260925.md`.

## Implemented result

The private disposable `HeldTerminalEvidence` in
`FileTransferTerminalIntegrityEvidenceTests.HeldFiles.cs` acquires the original
log, source and destination with read-only handles that deny write/delete
sharing. It records native identities, checks exact lengths before hashing,
validates the genuine owner/capture, original terminal rows and log descriptors,
and measures complete file hashes using those held handles.

Both original-primary tests now keep all three handles alive throughout
collection, original analysis, successful owned child exit and result checks.
Closing measurements use those same handles, not reopened paths. The source
binding's existing log predicate was extracted as `CheckBoundLog` so the held
owner and prior source-binding tests use the same checks. No terminal predicate
or expectation was relaxed.

Wrong owner references are rejected without invalidating the legitimate owner.
A missing/unavailable handle or failed validation retires the held owner;
partial acquisition releases every opened handle. Disposal is idempotent and
old owners cannot reopen or revive themselves. The primary harness also waits
up to five seconds for its owned parent process to exit after cancellation;
that failure path emits no successful held result. The timeout branch was not
forced in these runs.

This guarantees only the **post-finalization acquisition interval**. It does
not certify identity continuity before acquisition, immutability after disposal,
or cross-process ownership by the primary. Diagnostic JSON remains output-only.

## Verification

All **13 distinct focused tests passed**, with no failures or skipped tests:

- `artifacts/held-terminal-files-20260925/results/held-files-focused.trx`:
  three new held-file tests.
- `artifacts/held-terminal-files-20260925/results/held-files-primary-source.trx`:
  both original-primary integrations plus eight existing source-binding/token
  regressions, including actual cancellation and changed/replaced/missing logs.

The new tests exercise 23 named ownership, acquisition and lifetime rejections:
eight lifetime controls, twelve invalid/altered/partial-acquisition controls,
and three cross-attempt replay controls. Replay uses two genuine completed
transfers with identical payload bytes; relabeling a foreign capture is rejected.
The altered-file controls restore their fixture-owned files after each case;
they are controlled mutations, not independent production histories.

Write-open, rename and byte-identical replacement attempts are actually denied
for each of the three held files. The lifetime case checks this once; each
primary case checks before launch and after child exit (45 denial assertions
total). After disposal, exclusive writable opens and rename/restore operations
succeed, while the old owner remains invalid. Closing each of the three handles
in turn also retires the owner and releases the other handles.

New captures under `artifacts/terminal-source-tokens-20260925/`:

| Case | Attempt directory | Named rejection controls |
| --- | --- | --- |
| Lifetime/protection | `8e42adb5-b0d3-4bf6-aded-ae48fddf4a0c` | 8 |
| Altered/partial acquisition | `e25f44d8-da3d-4df7-abff-b8537ed7e9e8` | 12 |
| Foreign owner/replay | `54f23fa3-e89c-483e-be7d-026255c18229` | 3 |
| Windows PowerShell 5.1 primary | `17887d9d-2e45-4832-8d82-0cf73a24593f` | Existing 6 custody controls |
| PowerShell 7 primary | `f0f9e511-d966-411c-af49-30124979d2a4` | Existing 6 custody controls |

After the tests, all 15 files in the five held-result reports were independently
reopened and checked: native identities, byte counts and SHA-256 still match.
This extra check corroborates the reports; it is not an input capability.
Each measured payload file remains exactly 262,144 bytes.

Both primary integrations retain 93 source records and the two exact original
terminal rows. They preserve three authority evaluations, six custody controls,
unchanged views, `GuiCompleted=true` and `GuiIntegrityOk=false`. Their original
verdict stays `FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`. Cumulative diagnostic
work was 75 ms on PowerShell 5.1 and 94 ms on PowerShell 7.

## Remaining boundary and preservation

The held owner exists only in the C# fixture. Original-primary status remains
`verified_primary_terminal_source_only`, first missing edge
`file_owner_handoff_unavailable`, terminal-integrity gate `unproven`, and writer
membership unverified. `FileOwnerHandoffProven=false` is explicit in the new
`nlink.test-held-terminal-files/v1` diagnostics. No primary success, alternative
acceptance or whole-scenario receipt is issued.

Next is the separately scoped native owner adoption/primary join in the reviewed
handoff contract, respecting successful GUI exit, protected adoption, original
source/evaluation custody and writer membership. Canonical live-256 scope,
complete five-route cycle and independent-failure inventory remain separate.
Do not combine these captures with earlier Tuna-pair attempts.

Only two existing test partials changed, one test partial and this document were
added. All 103 unrelated baseline dirty files are byte-for-byte unchanged. Of
406 previous verification pins, 403 remain unchanged; the expected changes are
the two edited test partials and rebuilt test DLL. Production, PowerShell
support scripts, acceptance and historical artifacts are unchanged.
`git diff --check` and touched-file whitespace checks pass. The verification
seal is `artifacts/held-terminal-files-20260925/FINAL-VERIFICATION.json`.
No aggregate lane, portable build, live run, full matrix, commit or push ran.
