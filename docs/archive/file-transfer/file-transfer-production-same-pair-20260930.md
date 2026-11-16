# Production receiver receipt check

The normal loader now includes the existing check that connects the original
receiver receipt and witness batch to the held canonical payload pair.
Seventeen offline tests passed, including one fresh 256 MiB canonical-tail
transfer. This establishes the diagnostic check, not live route acceptance.
The production analyzer invocation remains a separate integration step.

## Scope and preservation

This bounded implementation was verified on 2026-09-30. The predecessor is
`artifacts/production-payload-primary-20260930/FINAL-VERIFICATION.json`,
SHA256 `86c20e9cb929e5624d6ff147be6cc829ef46190facd6a76438926fcd07628b33`.
All 15,349 predecessor pins matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the starting
worktree had 672 dirty entries.

Before-images preserve seven edited text files, the removed test-only helper,
and five pinned build outputs. The removed helper's logic moved into
`tools/FileTransferOps/LocalFrontierCanonicalSamePair.cs`; the phase verifier
checks equivalence after only removing the test compilation wrapper, renaming
identifiers, and updating its diagnostic-only comment. Earlier reports, seals,
and unrelated changes are preserved.

## Original ownership and receiver matching

`TryCheckCanonicalSamePairWitness` requires the original evaluation custody
in its taken state, the exact bound transfer evaluation and issued pair, the
original resolved witness batch, and that batch's exact native receipt payload.
Foreign references cannot consume the original one-shot attempt.

The check matches the original helper receiver's process and creation identity,
request, writer, and terminal sequence. Both writers must match the held pair.
The receiver receipt's session and transfer evidence tokens must match the pair.
The sender and receiver files must be distinct and retain the canonical hash.
The completion and marker are single-sequence ranges, ordered before the
receiver's drain. A sender payload cannot substitute for the receiver payload.

The existing native currentness checks run before and after comparison, under
the original cumulative work and visit budget. An admitted failure closes the
inputs. The code does not reopen files, rehash payloads, parse artifact JSON,
resolve another witness batch, or reevaluate analyzer authority.

This is a Boolean diagnostic check, not a new capability. The transfer and
witness types retain their false live-proof and publication flags. Coordinator
authority, route invariants, progress, throughput, limits, and acceptance rules
are unchanged. Fault injection and resolution diagnostics remain test-only.

## Verification

Both new normal-loader regression cases first failed with
`Production same-pair witness check missing`. Both passed after integration.
The build completed with 339 existing warnings and zero errors.

The focused lane passed 7/7: normal API and false-authority checks on both
PowerShell hosts, 29 scope and 16 ownership controls on each host, and the
original-budget/reference/no-extra-analysis source contract. The 90 nested
controls are not counted as additional xUnit tests.

The affected lane passed 9/9: original call ordering, exact receipt and batch
arguments, failure cleanup, unchanged route verdict, receipt/proof/handoff
controls, and both normal payload-binding loaders.

One fresh offline canonical-tail test passed in 7m20s. Its evidence is under
`artifacts/canonical-tail-native-characterization-20260929/pwsh-a7302e2756334974b8d3c5e2d83fb165`.
Both original peers transferred 268,435,456 bytes on `post_tuna_fallback_v6`.
The original analyzer ran three evaluations and one native read. One resolver
invocation produced 28 exact witnesses. `SamePairJoined` and
`OriginalOutputCommitted` are true. The original verdict remains
`FAIL_PROTOCOL_OR_INTEGRITY` / `fail`, and the selected failure remains
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.

The native test verified exclusive access to both released payload files and
the expected SHA256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
The observation records zero open diagnostic pins and cleared payload/witness
text. The phase verifier independently checks both files, all ten original
report hashes, recorded child-process exit, exact dirty-state preservation,
script syntax, whitespace, and the unchanged live entry.

## Next integration boundary

Wire the production analyzer to bind and check the original payload pair in
its optional post-report diagnostic path. Use the receipt already selected by
the original resolver, without another parse, selection, or authority
evaluation. Preserve original reports, honest omission, conditional output,
cleanup, and default-off live ownership. The current positive offline test
invokes the production check through the test probe; it does not establish
that production invocation is wired.

The live entry remains `Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`.
No portable rebuild, live NKN, live-256, full desktop GUI matrix, commit, or push
occurred. The required second coordinator-published `file_tuna_v4` route remains
a live correctness gate; neither fallback completion nor this diagnostic pass
satisfies it.
