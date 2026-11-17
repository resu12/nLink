# Wrapper payload/native-writer association — 2026-09-26

## State and acceptance boundary

The opt-in offline profile now carries the original GUI-owned source/destination
pair through the original native writer finalizer and associates it with the
wrapper's exact accepted `FrontierParentCustody`. The PowerShell 7 positive and
foreign-writer controls pass. Windows PowerShell does not yet reach this gate:
its original GUI cumulative work budget expires during parent facts publication.
This milestone is therefore **not accepted across both hosts or live-ready**.

There was no live NKN/Tuna run, portable rebuild, full GUI matrix, commit, push,
route/acceptance relaxation, or analyzer invocation in this step. The fixtures
use actual offline 256 MiB DevLocal transfers with the original process tree.

## Implemented association

- The explicit test path now uses the existing owned cursor/recovery processor,
  semantic copy, persisted evidence, native parent rendezvous and completion.
  These occur before the fixture deliberately retires its GUI writer owner.
- The wrapper first takes and hashes its original native payload pair. Only the
  exact remembered GUI reports and exact registered native custody can enter
  the new one-shot operation. It invokes the original `TakeAfterExit`; no
  completion is manufactured from logs or serialized labels.
- The accepted native manifests and writer inventory must match both original
  app request IDs, native PIDs/incarnations, writer IDs and terminal sequences.
  The carried payload labels are comparison evidence, not custody authority.
- The pair may cross the request's legacy closed event only inside that exact
  original terminal take, and then only while its accepted native writer owner
  remains current. Native commit-channel cancellation independently closes both
  payload handles. Copied owners cannot consume the take or dispose originals.
- The original wrapper cumulative work clock is reused for hashes, terminal
  acceptance and association. GUI and wrapper five-second limits are unchanged.
  All live-proof, transfer-integrity and publication permission flags remain
  false. No public payload reader or downstream relay capability is issued.

## First RCA and bounded fix

The first PowerShell 7 run completed the payload offer but failed the newly
exercised native finalizer. Stage diagnostics narrowed it to acknowledgment
confirmation after files/facts publication, at 5002–5003 ms of GUI active work.
A temporary test-profile measurement recorded **829 full facts checks taking
2247.2671 ms aggregate elapsed time** in
`pwsh-writer-valid-e0bdf47792e14b82886bc746d16f7174`.
This timing includes the checks and their callbacks; it is not a CPU profiler.

The only normal-loader change is in `LocalFrontierParentFactsChannel`:
the same entire mapped packet is compared in 32 KiB blocks of machine words,
with explicit trailing-byte comparison. Expected words are copied from the
actual retained body each time; no cached success verdict or stale secondary
snapshot replaces validation. Publication/open still validate all zero padding.
Header/state/cancellation checks and callback cadence are unchanged. Temporary
timing instrumentation was removed after diagnosis.

After this change, the PowerShell 7 full path completed at 4430 ms in the first
positive rerun. The final lifecycle run completes at 4462 ms within the unchanged
cap and verifies handle closure without querying payload currentness to trigger it.
Separate comparison controls cover body sizes 1, 7, 8, 9, 32767, 32768, 32769 and
1048577; representative first/last, word/block-boundary, retained-body and padding
mutations; cancellation and disposal. Both PowerShell hosts pass those controls.

Two development-only fixture errors were also corrected: a dot-sourced
`Stage` validation attribute collided with a diagnostic loop variable, and a
PowerShell delegate could not see a private variable in the release assertion.
These are retained as failed artifacts, not described as runtime failures.

## Remaining first edge: Windows PowerShell GUI budget

The final Windows positive reached semantic copy at 4777 ms, persistence at
4816 ms, then failed parent preparation at 5001 ms:
`powershell-writer-valid-ff272c07132c4e43807e05e2c729ba0f`.
The foreign-writer control independently reached copy/persistence at 4634/4674
ms and the same 5001 ms cutoff:
`powershell-writer-foreign-437499b9ab5f42c5b3c739dc4a4e8ce2`.
Both entered parent facts publication; neither entered acknowledgment
confirmation or completion. Neither reached the new association or its injected
foreign-origin check. These two cases remain **failed**, not negative-control
passes. The earlier accumulated Windows cost has not yet been attributed to a
specific operation; do not call it a transport failure or assume the prior
intermittent role-join rejection has been explained.

Next bounded plan:

1. Measure Windows GUI work at the existing original seal, owned processing,
   role/source/destination joins and payload offer boundaries. Preserve the
   same source reader, native owners and cumulative clock. Distinguish fixture
   probes from actual handoff work without bypassing either validation contract.
2. Fix only the first measured hot path, if it can retain all native and byte
   checks. Do not increase/reset budgets, accept expired evidence, reopen files,
   adjust transfer parameters, or grant authority from observer state.
3. Rerun the two blocked Windows cases and directly affected controls. Do not
   rerun unrelated aggregate lanes merely because this edge was discovered.
4. Only after both hosts pass, implement wrapper-to-original-parent payload
   adoption before wrapper cleanup. Primary/analyzer consumption remains later.

The selected-failure pre-verdict reservation and default live dispatch guard
remain unchanged. SHA success never replaces the required second
coordinator-published `file_tuna_v4` route.

## Verification and recovery

There are 24 distinct passing tests (25 passing executions) and eight retained
failed development executions across 33 executions. The two Windows association
cases remain unresolved failures. Earlier PowerShell 7 failures were superseded
by the final positive and foreign-origin passes, not deleted.

| Result file | Pass / fail | Meaning |
| --- | --- | --- |
| `facts-word-comparison.trx` | 2 / 0 | Both hosts; complete word/block/tail/padding comparison controls. |
| `writer-positive-initial.trx` | 0 / 1 | First newly exercised native handoff failure. |
| `writer-positive-diagnostic.trx` | 0 / 1 | Diagnostic loop variable collided with a dot-sourced validation attribute. |
| `writer-positive-diagnostic-stage.trx` | 0 / 1 | Original GUI budget expires during preparation. |
| `writer-positive-native-gate.trx` | 0 / 1 | Narrowed to acknowledgment confirmation. |
| `writer-positive-facts-profile.trx` | 0 / 1 | Recorded 829 checks / 2247.2671 ms. |
| `writer-positive-word-comparison.trx` | 1 / 0 | First full PowerShell 7 association after the bounded comparison fix. |
| `writer-association-final.trx` | 1 / 3 | PowerShell 7 foreign-origin rejection passes; both Windows cases hit budget; PowerShell 7 cleanup assertion had a private-variable scope error. |
| `writer-pwsh-lifecycle-final.trx` | 1 / 0 | Final PowerShell 7 positive, copied/repeated inputs and independent handle retirement. |
| `writer-focused-compatibility.trx` | 20 / 0 | Selected native facts correctness/budget controls, prior payload positives, default-loader isolation; both hosts. |

The previous wrapper checkpoint verified all 819 pins before work. Test results,
development failures and the current source inventory are retained under
`artifacts/payload-writers-20260926`. The recovery seal is
`artifacts/payload-writers-20260926/FINAL-VERIFICATION.json`; its verification
table is authoritative for exact test counts and source/artifact hashes.
No old artifacts or unrelated edits were removed. Branch remains `v0.7.0` at
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Six pre-existing dirty support files changed; the other 292 remain byte-for-byte
unchanged. One previously clean normal-loader file and five new support/test/report
files bring the dirty inventory to 304. Nothing was staged.
