# Actual payload pair to native primary — 2026-09-27

Status: native primary/evaluation handoff implemented and verified offline;
selected-witness consumption remains unproven. Compatibility checks passed.

## Scope and contract

The preceding checkpoint verified the original wrapper-to-parent hop. The next
missing edge is the native primary preparation: it deliberately retires the
parent's writer custody, which also retires the old payload pair. Duplicate the
two already-held read-only file handles into the exact native primary inputs
before that retirement. Do not exempt the old owner from cleanup.

Keep this extension inside `NLINK_TEST_ATTEMPT_DISPATCH`. Existing native request,
worker binding, reports, source passes and evaluation objects remain the owners.
The actual 256 MiB source/destination files are separate from the diagnostic
receipt catalog and its 256 KiB cap. No process/file reopen, caller-created file
owner, replacement analyzer or new coordinator evaluation may establish custody.

After the original worker is bound, export a bounded metadata packet under its
native post-bind scope. The worker duplicates only from its already-held parent
process handle, matches the GUI completion and writer facts, and checks native
file identity and length. Reuse the fixed 8192-byte channel; this hop is an
acknowledged offer held for the native request lifetime, not another terminal
commit/retirement handshake. There is no extra rendezvous or active-work budget.
Charge duplication and binding to the existing parent/primary/worker clocks.

Bind the pair once to the original native full/compact evaluation pair, arrays,
context and views. Reject copies, foreign arguments and retired owners. Native
evaluation/request retirement must close both duplicates independently of a
later currentness query. The binding is evidence custody only: live proof,
transfer integrity and publication flags remain false.

Use the original analyzer via its existing parent launch. Assert the existing
three coordinator evaluations and one native source read remain unchanged.
Ordinary analysis must not be replaced by a test evaluator or by copied result
objects. A missing or invalid optional transfer pair cannot confer ownership.

Selected failure witnesses still require the original native resolver and its
same-transfer diagnostic receipt. Do not manufacture a failure/witness merely
to obtain a positive test. This step closes the pair-to-evaluation boundary;
selected-witness consumption is assessed from the resulting actual analyzer
evidence, with no acceptance or pre-verdict change.

## Verification plan

Run full-size offline DevLocal positives on both PowerShell hosts, plus wrong
completion, retired pair and retired native-owner controls. Check copied/repeated
evaluation inputs and independent cleanup. Verify ordinary loaders expose none
of the new APIs, and run only directly affected compatibility controls.

No live GUI/NKN run, portable rebuild, full matrix, commit or push is authorized
by this bounded step. All route-cycle acceptance requirements remain unchanged.

## Observed boundary

All five full-size cases passed in `primary-full-size.trx`: Windows PowerShell
and PowerShell 7 positives, plus PowerShell 7 foreign-completion, retired-pair
and retired-native-owner controls. The real analyzer still performs exactly
three coordinator evaluations and one native source read. It rejects copied
custody, copied evaluation pairs, cloned source arrays and foreign contexts;
the valid original binding is one-shot. Native retirement closes both worker
duplicates before any later currentness query, and both final files can be
opened exclusively after the process tree finishes.

The source fixture currently selects only `operational_log_writer_drained`.
Consequently, the real primary sees **two source lines and zero diagnostic
receipts**. The original parent returns `proof_incomplete`, with
`completedProofArtifacts=false` and `LegacyV0` snapshot mode. The test asserts
and rethrows that exact original rejection; it does not turn it into accepted
route-cycle evidence. A successful custody test is not a successful route test.

Initial runs exposed fixture gaps before this final result: the ordinary
retained-log copy was absent, then its reader sharing was incompatible with
the native duplicates. The fixture now uses the existing atomic-copy helper
with its read-sharing option and verifies that a new writer remains blocked.
The copied ordinary artifact is only a compatibility input; native held files
remain the custody source. A subsequent run reached binding/cleanup correctly
but exposed the expected incomplete-proof rejection; assertions were corrected
to retain that rejection, without modifying production acceptance.

## Next missing proof edge

Provide the original primary with actual same-attempt correlated source records
and its native diagnostic receipt, so its own selected failure can resolve an
exact native witness. Bind that original witness to this evaluation-owned
transfer pair, matching session/transfer scope and existing lifetime limits.
Do not synthesize a reported failure, inject a caller-selected witness, treat
the binary payload as a diagnostic receipt, or introduce another evaluation.
This requires another bounded offline integration before live enablement.

The normal GUI dispatch remains unchanged and does not pass `-FrontierOwner`
to `Run-ScenarioFileTransferTunaHandoffFallback`. No live acceptance, second
Tuna publication, transfer-integrity authority or publication completion is
claimed by this checkpoint.

## Final verification and preservation

- **12 distinct final tests passed, zero failed/skipped:** five full-size primary
  cases; four original relay analysis/changed-source controls across both hosts;
  one upstream PowerShell 7 payload-parent positive; two default-loader guards.
- Six changed PowerShell scripts parse successfully. Scoped `git diff --check`
  passes. The four native helper files contain only 15 conditional hook lines;
  the implementation remains excluded from the default loader.
- All 350 previously dirty files were accounted for: four scoped fixture files
  changed, and the other 346 remain byte-identical. Ten newly dirty/new files are
  all in this step's scope, for 360 current dirty entries. No unrelated edits.
- Final results: `artifacts/payload-primary-20260927/results/primary-full-size.trx`
  and `primary-compatibility.trx`. Earlier failing fixture runs are retained
  separately and are not counted as successful route evidence.
- Checkpoint: `artifacts/payload-primary-20260927/FINAL-VERIFICATION.json`, rooted
  in the preceding payload-parent seal. HEAD remains
  `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.

No new portable build, live run, matrix, commit or push was performed. Existing
budgets, diagnostic receipt cap and acceptance rules are unchanged.
