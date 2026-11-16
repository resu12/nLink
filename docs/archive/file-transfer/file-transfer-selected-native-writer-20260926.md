# Original selected witness with native collector custody

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-native-witness-writer-20260926.md`.

## Implemented boundary

The small, existing seeded-failure fixture now reaches the original selected
failure witness and original publication completion using its own continuously
held native collector outputs. This closes the acquisition/integration gap from
the preceding milestone; it does not prove terminal integrity or live acceptance.

`SeededCollectorLogForTest` is a separate log-only owner, not an instance of the
three-file terminal owner. It opens the fixture log once with write/delete
sharing excluded, and duplicates that handle to the actual bound collector.
The existing GUI collector/finalizer captures and forwards its four outputs.
The accepted completion, native source hash lease, producer identity and exact
original source request are bound before the primary worker starts.

The primary handoff checks request ID, PID/start time and the hash of the original
bounded request descriptor. The existing collector packet verifies all four
native identities and the original producer mapping. After acknowledgement,
the parent closes all of its output/source duplicates and verifies that the
primary's handles still block writes before releasing analysis. The fixture
log remains held by its separate owner until the harness finishes.

Under the explicit `NLINK_TEST_SELECTED_NATIVE_WRITER=1` profile, only the
original witness's writer-acquisition call changes. It uses the preceding
native adapter; the original membership verifier, witness checks, report and
publication completion still run. One successful membership instance retains
both the original collector position map and the borrower inputs. The borrower
also retains and rechecks the exact adopted collector owner.

The shared source edit is a new, explicitly test-guarded membership-return
branch. Normal production and existing test profiles keep their prior branches.
The new profile requires the adapter, borrower and collector-primary selectors,
and rejects terminal-primary, collector-membership, terminal-payload,
pre-verdict-integrity and terminal-source-probe combinations before native
compilation or analysis. The earlier unsupported borrower/terminal-profile
guard remains intact. No pre-verdict reservation or acceptance rule changes.

## Focused proof

The final focused run passes all five tests: active, disposed and deliberately
unavailable borrower cases (each on Windows PowerShell 5.1 and PowerShell 7),
plus eight rejected profile combinations on each host. The earlier three-case
run also passed. Final positive artifacts are under
`artifacts/selected-native-writer-20260926/`:

- `03617ae34d8c48b191ee82fa025aabf7`: active borrower.
- `30c30e73b1d7489b9c5ab949c07d42f7`: disposed borrower.
- `91a634f3f5e244d28a1366dbd0421586`: unavailable borrower.

Each positive uses 11 source records, five compact records and one 21-record
writer EOF/hash pass. The original reported failure selects index seven and
writer line 15, not the old index-zero lookup. Both map fields refer to the same
original retained array. Active/disposed consumption uses 161 cumulative
record visits, 133751-133826 code units and 231-392 ms of cumulative work; no
budget is increased. Borrowing adds no writer read or authority evaluation.

The existing 12 pre-borrow and 22 consumption controls remain effective. The
combined probe additionally rejects a copied adopted collector owner, repeated
native acquisition with the original clock running, and legacy reopening.
These reflection substitutions are predicate controls, not alternative histories.
The original collector harness and new handoff reject copied owners/requests,
unbound processes, repeated send/adoption, and repeated source opens.

Disposing or omitting the borrower preserves the original witness, verified
reader and publication completion. Original context cleanup retires the reader
and borrower before the four collector handles close; the source worker then
retires. The harness verifies that output files become writable after cleanup.

All positive runs retain exactly three original evaluations and the failed
`FAIL_PROTOCOL_OR_INTEGRITY` verdict, `fail` gate and
`file_tuna_data_path_mismatch` first proof failure. The fixture contains a seeded
transport observation; it is not a completed Tuna transfer or live network run.
No terminal file owner, payload join or whole-scenario acceptance is inferred.

## Verification and preservation

The test project builds and the isolated native profiles compile on both hosts.
An initial build attempt stopped at Avalonia's sandbox-disallowed telemetry log;
the build succeeded with the optional `UsedAvaloniaProducts` telemetry target
disabled for that command only. No project/runtime setting was changed for it.

The one focused compatibility lane passes 15/15: original borrower behavior,
the earlier mixed-profile refusal, production/default-terminal API absence,
native writer acquisition, collector membership and collector-primary reads.
All 20 distinct targeted tests pass; the three TRX files contain 23 executions,
all passing with no skips. No aggregate contract/analyzer suite was rerun.

Eight existing test/support or test-guarded source files change; three new
test/support files and this report are added. The other 177 baseline dirty files
are unchanged. All 14691 effective inherited pins were checked: 14682 match;
the nine expected differences are the eight edited files and rebuilt test DLL.
HEAD/branch remain unchanged, with 189 dirty entries. PowerShell parsing and
`git diff --check` pass. The final seal is
`artifacts/selected-native-writer-20260926/FINAL-VERIFICATION.json`.

## Next boundary

Bind a post-witness collector-origin result to this exact retained membership,
original failure and live owner without another writer traversal. A subsequent
terminal/payload consumer still needs a genuine same-attempt terminal fixture;
this log-only fixture must never supply one. Keep that separate native-terminal
combination disabled until its positive exists. This milestone is not live-256
readiness. No portable build, live GUI test, full matrix, commit or push is run.
