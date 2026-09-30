# Post-witness selected collector-origin binding

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-selected-native-writer-20260926.md`.

## Completed boundary

The original selected-failure borrower can now issue a non-owning collector-origin
result after the original witness, publication completion and reports. It uses
the existing default-off selected-native test profile. The observation is enabled
only by `NLINK_TEST_SELECTED_ORIGIN_JOIN=1`; no production loader or runtime file
changes in this step.

The join requires the exact current borrower, its successfully consumed original
selection, adopted collector owner, original primary custody/evaluations/inputs
and private publication completion. The completion must still own the same
verified writer. No caller-authored row, line selection, JSON, digest or prepared
witness object enters this API. The retained original membership supplies the
selected index and source coordinate, and its original writer-position map must
match the collector's producer/retained/writer mapping.

The result rechecks these lifetimes and the selected mapping. Copied results and
owners cannot substitute for the originals. Disposing the result closes no file,
reader, borrower or publication. Disposing the borrower, retiring the original
completion or original context/worker cleanup makes the result unusable.
Detached coordinate properties remain diagnostics, not input capabilities.

One admitted join adds one selected metadata lookup to the original cumulative
record count. It performs no writer traversal, source read, file hash or authority
evaluation. The original clock, 4096-record ceiling and code-unit budget remain
in force. An invalid membership/collector mapping spends the one join attempt;
restoring the map cannot reopen that attempt. Invalid owners/completion, an
unconsumed selection, stopped/expired clock or exhausted budget are rejected
before admitting the lookup.

## Verification

All 19 distinct targeted tests pass, with 22 passing executions across three TRX
files and no failures/skips. Five new cases each run on Windows PowerShell 5.1
and PowerShell 7: active result, result disposal, borrower disposal, invalid map
and unavailable borrower. After adding explicit wrong-coordinate controls, only
the three affected positive cases were rerun. One 14-case compatibility lane
covers the original borrower/native path, both profile refusals and production/
default-terminal API absence. No aggregate suite or GUI matrix was run.

Final positives are under `artifacts/selected-collector-origin-20260926/`:

- `4cb8e3bf6c9b44d0abfc4af8616904c5`: active result.
- `b0b57021bbf2468aa71742812cffd68f`: result disposal.
- `c3b3d2857f7e4b35baea3681b9e03504`: borrower disposal.

The original failure selects index seven: source line eight maps to writer,
retained-log and producer line 15. Each positive retains one 21-record writer
EOF/hash pass, 11 source records, five compact records and three original
evaluations. The join raises cumulative visits from 161 to 162, with 133826 code
units and 281-473 ms cumulative work in the final positives. Original views,
witness and completion remain unchanged/current, and the original
`FAIL_PROTOCOL_OR_INTEGRITY` verdict, `fail` gate and
`file_tuna_data_path_mismatch` first proof failure remain unchanged.

Nine pre-join and 13 post-join predicate controls cover copied/missing inputs,
unconsumed selection, clocks/budgets, raw/event changes, collector/publication
retirement, and wrong source/writer/producer coordinates. Reflection mutations
are explicitly predicate controls, not genuine alternative histories. The
separate invalid-map case proves a spent failed attempt. Real result/borrower
disposal and original context/worker cleanup prove non-ownership and retirement.
The unavailable case issues no origin and preserves the original completion.

## Preservation and scope

The previous seal's 1009 pins matched before work. All 15691 effective inherited
pins were checked; 15685 match, with the five edited test/support files and rebuilt
test DLL as the six expected differences. The other 184 baseline dirty files are
unchanged. Four test/support files and this report are added; there are 194 dirty
entries. HEAD/branch are unchanged. The focused build, both isolated native
compilers, PowerShell parsing, whitespace checks and `git diff --check` pass.
The final seal is
`artifacts/selected-collector-origin-20260926/FINAL-VERIFICATION.json`.

This is an offline seeded-failure origin binding, not terminal integrity,
successful Tuna transfer or live acceptance. No payload owner is fabricated,
no terminal/payload consumer is enabled, and no acceptance/pre-verdict guard
changes. No portable build, live-256 run, full GUI matrix, commit or push occurs.

## Next step

Provide a genuine single-transfer fixture with its actual service/coordinator
publication, selected failure and both completed terminals/payloads. Any seeded
failure must come from that same private owner before its logger drains. Do not
merge this standalone seeded fixture with the separate successful Regular-v4
fixture. Require the same-transfer positive and equal-byte foreign-transfer
rejection before enabling the post-witness terminal/payload join. The existing
mixed terminal-profile refusal stays in place; live-256 is not yet the next gate.
