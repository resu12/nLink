# Native files: original parent request association

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-native-parent-files-20260925.md`.
Offline, test-only parent association; not a primary file-integrity handoff.

## Implemented boundary

The actual parent can now associate its adopted three-file owner with its
original retained-source request, accepted writer completion and verified
retained-source lease. The test-only native partial checks the original request
and writer references through the existing `FrontierScopeCurrent` lifetime
check, the writer's actual local acceptance, matching run/scenario, and the
native identity and length of the request's frozen source. It records those
exact objects, not exported JSON or request IDs alone.

The file owner permits one request claim. A failed request-binding attempt
cannot be retried; another genuine request cannot reuse the claimed owner.
Copies of the request, file owner or binding cannot act as the original, and
disposing copied file/binding objects does not dispose the originals. A different
lease for the same underlying retained file cannot replace the lease recorded
by an established binding.

The association remains current when the original request binds its real primary
worker. It becomes invalid when that worker exits and remains invalid after
request/lease/binding disposal. The adopted file owner itself stays held until
the outer parent's existing cleanup; invalidating an association does not close
someone else's file handles. The existing C# fixture still verifies mutation
denial after producer-handle retirement and release after parent exit.

`TerminalRequestFileBinding.cs` is guarded by `NLINK_TEST_TERMINAL_REQUEST` and
compiled only by the new opt-in test loader alongside the unchanged native tool
sources. The production loader, native request implementation and codecs are
unchanged. The original primary child uses the ordinary production loader and
does not receive the parent association. The normal native-parent and non-native
primary paths remain opt-out and emit no request-binding report.

## Verification

All **seven distinct targeted tests passed**, with no failures or skips:

- `artifacts/native-request-binding-20260925/results/native-request-focused.trx`:
  the two new original-request association integrations, Windows PowerShell 5.1
  and PowerShell 7.
- `artifacts/native-request-binding-20260925/results/native-request-contract.trx`:
  both previous native-parent integrations, the native malformed/foreign packet
  controls, and both ordinary original-primary integrations. This relevant lane
  ran once; unchanged held-file and aggregate lanes were not rerun.

Each new integration checks 17 named rejection cases: copied request/file owner,
null/copied file input, copied/missing writer, unverified/closed lease, closed
request, request-copy/foreign-genuine-request substitution, file/writer-copy
substitution, another lease for the same file, copied binding, repeated binding
and reuse of the file owner by another request. Failed acquisition is one-use.
Post-worker-exit invalidity and final cleanup are also checked.

New successful attempts under `artifacts/terminal-source-tokens-20260925/`:

| Host | Attempt | Original evaluations | Full / compact | Diagnostic work |
| --- | --- | --- | --- | --- |
| Windows PowerShell 5.1 | `60560cf1-817b-405f-936f-2bffd39490d6` | 3 | 93 / 16 | 74 ms |
| PowerShell 7 | `f9edf4aa-da0f-4863-99d7-98da69dc300b` | 3 | 93 / 16 | 86 ms |

Both preserve the exact two real terminal records, six existing primary custody
controls, unchanged original views, `GuiCompleted=true`, `GuiIntegrityOk=false`,
`FAIL_PROTOCOL_OR_INTEGRITY` / `fail`, and unverified writer membership. No extra
authority evaluation, observer-side file read, budget increase or integrity
inference was introduced. Control-only same-file leases are measured before
launching the primary, not inside its diagnostic interval.

## Explicit limits and next step

The output status is `verified_offline_parent_request_association_only`.
`PrimaryFileOwnerHandoffProven=false`; the original primary still reports
`file_owner_handoff_unavailable` and an unproven terminal-integrity gate.
This step does not establish native file adoption by that primary or bind the
files to its completed full/compact evaluation objects.

In particular, `LocalGuiWriterWorker.ps1` copies `GeneratedSource` into its own
collector source using `ReadAllLines`/`WriteAllLines`. An accepted writer context
therefore is not native provenance from the held producer log into that copy.
This milestone does not claim that association, matching scope, matching hashes
or matching terminal records close that provenance/membership gap. Its report
explicitly leaves `WriterCopyProvenanceProven=false` and
`WriterMembershipVerified=false`. No alternate writer or source is introduced
to make either claim pass.

Next is the request-owned hop into the actual original primary, using its
existing source/custody lifetime rather than a JSON import. Before any complete
terminal-integrity claim, the producer-log-to-writer provenance and owned writer
membership must also be established. Do not invent a failure witness or reopen
the one-shot writer reader in the observer. Preserve the original evaluation
count, cumulative work limits and independent failures.

Producer process death, cancellation during native adoption and a production
GUI-owned file issuer remain outside this test milestone. The association's
post-binding lease/file-loss branches are not independently fault-injected by
these two integrations; their exit/cleanup checks must not be described as those
additional lifetime tests. Canonical live-256 scope, five-route proof and the
independent-failure inventory remain separate obligations. No cross-attempt join
or acceptance change is authorized by these results.

## Preservation

All 946 previous pins matched before work. Six existing test files changed and
three test support files plus this document were added. All 105 unrelated
baseline dirty files are preserved byte-for-byte. Of the previous pins, 939
remain unchanged; the expected seven changes are those six test files and the
rebuilt test DLL. Historical evidence and production files are unchanged.

PowerShell parsing, `git diff --check` and touched-file whitespace checks pass.
The verification seal is
`artifacts/native-request-binding-20260925/FINAL-VERIFICATION.json`.
No portable build, live run, full matrix, commit or push was performed.
