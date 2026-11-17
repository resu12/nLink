# Production analyzer payload pair integration

The production analyzer now binds the held payload pair and invokes its
receipt check through the original native witness resolver. Seventeen offline
tests passed, including one fresh 256 MiB canonical-tail transfer. The test
probe observes this production result rather than performing the binding or
same-pair check itself. Live scenario ownership remains disabled.

## Reproduced integration gap

The four new regression cases failed on both PowerShell hosts with either
`Production pair invocation missing` or `Production receipt check missing`.
The earlier native implementation existed, but its production caller did not.

This phase began on 2026-09-30 from
`artifacts/production-same-pair-20260930/FINAL-VERIFICATION.json`,
SHA256 `8062cda387a9a538797c96991756c33f5713723049465776d3d8a215bc06deb2`.
All 15,585 pins and 673 dirty entries matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.
Before-images preserve seven edited text files and five pinned build outputs.

## Production changes

Only two production files changed. In `AnalyzerOrchestration.ps1`, the existing
optional diagnostic branch binds the original pair after the original reports
have been written. It passes the exact evaluation, context, arrays and result
views to `TryBindTransferPair`, then passes that association to the existing
resolver. The selected failure and eligibility condition are unchanged.
The original custody disposal remains in place, and the new local reference is
cleared in the same cleanup path.

In `LocalNativeFrontierWitness.ps1`, the callback retains references to the
receipt it already decoded and selected and its exact native payload. After
the original witness batch and physical ranges are verified, a supplied
transfer association must pass `TryCheckCanonicalSamePairWitness`. Refusal
returns no witness, records `native_frontier_same_pair_unavailable`, and retires
the batch; exceptions retain the existing omission and cleanup behavior.
No additional receipt parse, witness selection, or authority evaluation occurs.
The receipt and payload references are cleared on every resolver exit.

A null association preserves existing unpaired diagnostics and reports
`SamePairMatched=false`; it is not canonical-pair success. A supplied association
that fails the check cannot reach optional output. The new Boolean result is
diagnostic evidence only. Existing live-proof, native-writer-authority and
publication-completion flags remain false. No route logic, acceptance rule,
time limit, progress, throughput setting, or coordinator authority changed.

The canonical test probe now skips its pre-report binding operation and never
calls the same-pair checker. It requires the production association to be current,
checks its size/hash and false authority flags, observes `SamePairMatched=true`,
and then verifies retirement using that same association. Existing standalone
binding fault controls remain available.

## Offline verification

The final build passed with 339 existing warnings and zero errors.

Four focused tests passed on both hosts. Their nested controls verify exact
original binding arguments, one-shot post-report call order, supported-failure
gating, null association behavior, refusal/exception handling, output sequencing,
false authority flags, and receipt-reference cleanup.

The affected lane passed 12/12: source/probe separation, seven canonical
composition controls, two original unpaired native receipt cases, and both
callback-profiling compatibility cases. The four intentional red results are
retained and resolved by the same passing focused cases. No aggregate suite or
full GUI matrix was run.

One frozen-input offline canonical-tail test passed in 5m56.713s. Evidence:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-0efbfb6b267d4da99fc28c5816635943`.
Both peers transferred 268,435,456 bytes on `post_tuna_fallback_v6` with matching
SHA256 `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
The analyzer ran three evaluations and one native read. One resolver invocation
produced 27 exact witnesses. The production-check observation, same-pair match
and original diagnostic output commit all succeeded.

The original route verdict remains `FAIL_PROTOCOL_OR_INTEGRITY` / `fail` and
the selected failure remains
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.
The observation records zero open diagnostic pins and cleared receipt/witness
text. The test verified exclusive access to both released payloads and their
lengths/hashes. Diagnostic success is not route-cycle acceptance.

The phase verifier checks all predecessor pins, frozen inputs, exact dirty-state
preservation, test/build results, all ten original report hashes, released
payloads, recorded child-process exit, syntax, whitespace, and the unchanged
live scenario entry. Earlier reports and seals are not edited.

## Next boundary before live execution

The GUI prepares a canonical attempt, but its top-level
`FILETRANSFER_TUNA_HANDOFF_FALLBACK` dispatch still calls
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx` without passing
ownership. Next, review and connect that guarded dispatch using the original
canonical attempt, and verify capture-off and rejected-attempt behavior offline.
Do not enable it by forwarding an owner without the original attempt checks.

After that integration is verified, the remaining live preparation is a reviewed
clean checkpoint, fresh portable build and pinned provenance, then strict
escalated preflights for the separately scoped live attempt. No portable rebuild,
live NKN, live-256, full desktop GUI matrix, commit, or push occurred in this phase.
The second coordinator-published `file_tuna_v4` remains mandatory for live
route-cycle acceptance.
