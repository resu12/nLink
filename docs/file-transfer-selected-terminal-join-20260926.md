# Selected-witness terminal join

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-authenticated-payload-handoff-20260926.md`.

## Outcome and scope

The authenticated offline 256 KiB fixture now joins both exact completed
terminal records to its original selected-failure membership and adopted native
payload owner after the original reports. The selected index is not zero.
This establishes the bounded post-report positive; it does not make the earlier
route verdict pass or supply pre-verdict integrity.

The original `file_tuna_data_path_mismatch` remains
`FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`, with the same witness/completion and
three authority evaluations. Production source, acceptance and the selected
failure's pre-verdict reservation guard are unchanged. The fixture still uses
an explicitly seeded observation and fake network/Tuna transport. Its native
payload custody begins at post-finalization acquisition, not initial file
creation. This is not a two-GUI-writer or live-256 result.

## Implementation

Only the existing explicit authenticated-handoff test profile loads the new
helper. Ordinary loaders omit its API; mixed-profile refusals remain.

The original membership retains references to its event list and source-origin
array when the selected witness succeeds. The post-report borrower reserves
`full.Length + 2` remaining record visits, then traverses that original source
once using its retained membership positions and native collector origins.
The producer coordinates already inside the protected-file binding identify
the inbound and outbound records. Candidate terminal objects must be the exact
indexed originals, with original raw-string references, source path and line,
and matching source/writer/producer origins.

The two bounded parses require completed terminal events, correct directions,
the native session/transfer tokens, no error, and exactly 262144 transferred
bytes and file size. The existing held source/destination measurements must
still match. No path reopen, writer read, payload hash, authority evaluation,
clock reset or old collector/index-zero accounting formula is introduced.

All added visits and four times each parsed terminal line's code units accrue
to the original borrower counters. An admitted failure spends the one-shot
attempt even after its injected fault is restored. The non-owning result
depends on current original ownership, exact terminal references, origin maps,
native scope, held payload measurements, clock and budgets. Disposing it leaves
the original reader, borrower, payload association and completion current.
Original cleanup retires the association and closes all three producer files
and four collector outputs. Diagnostic JSON is output only.

## Focused verification

Nine focused cases pass: positives on Windows PowerShell 5.1 and PowerShell 7,
copied terminal, foreign scope, absent producer coordinate, swapped directions,
result disposal, and default API/profile guards on both hosts.

Each joined positive passes nine pre-admission controls, 22 stale/substitution
controls and 12 scalar parser controls. The parser fault injections are tests,
not another consumer join or authority evaluation. Existing borrower and native
handoff controls remain active. Successful joins add 246 or 247 visits, yielding
3072 or 3087 cumulative visits, and 7408 code units. Original cumulative work
times are 536, 595 and 721 ms, below the unchanged 20-second cap. There are no
additional writer reads or analyzer payload hashes.

Positive runs:

- PowerShell 7: `93b50d14-d890-4c2d-a7d7-73583ce7f94b`.
- Windows PowerShell: `51609866-3ace-4a48-822c-606dc3deb560`.
- Result disposal: `12a79940-c965-421d-8a26-4110c467110f`.

The first two development runs failed in the new probe's PowerShell collection
conversion and parser-control array construction. Both probe bugs were fixed;
their failed artifacts remain preserved. Final focused results are
`initial-fixed.trx` and `focused-controls.trx` under
`artifacts/selected-terminal-join-20260926/results`.

The single directly affected compatibility lane passes all 12 cases: five
previous authenticated handoff cases, three log-only selected-native cases,
the ordinary active origin and borrower cases, and the old terminal payload
join on both hosts. Together the final runs pass all 21 distinct tests, with
no failures or skips. No aggregate suite was run. The test build, both hosts'
PowerShell syntax checks, and whitespace checks pass.

All 999 preceding seal pins matched before edits. Nine existing test/support
files changed; the remaining 202 prior dirty files are unchanged. Three new
test/support files and this report bring the dirty-entry count to 215. The
incremental evidence seal is
`artifacts/selected-terminal-join-20260926/FINAL-VERIFICATION.json`.

## Next boundary

Review the real-GUI producer/parent handoff contract against current source:
exact parent attempt and launch arguments, both independent writer origins,
and continuously held payload identity through the designated parent and
original analyzer. Keep that separate from a pre-verdict ordering or acceptance
change. The offline fixture's 256 KiB size and single writer cannot establish
the canonical 256 MiB five-route cycle or its second published Tuna-v4 route.

No portable build, live network/GUI run, full matrix, commit or push was done.
The next step is this bounded handoff review, not a live-256 rerun.
