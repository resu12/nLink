# Corrected witness probe: native result and budget RCA — 2026-09-29

## Result

The corrected test-only probe works. One fresh offline 256 MiB native attempt **failed at the first witness ownership check because cumulative evaluation visits exceeded 4,194,304**. It did not exhaust the 20,000 ms work budget at that point.

No production source, test source, acceptance rule, or limit changed in this phase. No live NKN/GUI test, full matrix, automatic retry, commit, or push was performed. The next step is full-consumer budget sizing, not live acceptance.

## Verified checkpoint and run

- Branch `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
- Previous seal: `artifacts/canonical-witness-verification-native-20260929/FINAL-VERIFICATION.json`, SHA-256 `4757c56605a89f1871c5cf61957b178f9e3b3e4a9977d8b2d0e02d451a7c1796`; all 6,899 pins and 611 dirty entries verified at entry.
- Rebuilt the Core test harness outside the sandbox: **0 warnings, 0 errors**. Test DLL stayed `6eda39e46329aab9a64915b98807cf55ef627826e2bf42d043fa3b7418eedc27`.
- Unchanged focused evidence: **54 unique selected checks green across 73 executions**; no unnecessary test rerun.
- Frozen inputs: `artifacts/canonical-witness-verification-scope-native-20260929/RUN-INPUTS.json`, SHA-256 `128b78fd5bbeea34aaf66f6d5a4bade83f0f4bae377a1250f13ef825cffbf3dc`.
- Both strict preflights passed outside the sandbox, checking 6,904 pinned inputs, process isolation, prior focused evidence, and unchanged limits.
- Exactly one `FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary` execution; **failed after 6m 7.9765s**, no timeout.
- Fixture: `artifacts/canonical-tail-native-characterization-20260929/pwsh-2246b17104064afc8325c3939ac10bda`.

Both peers transferred 268,435,456 bytes on `post_tuna_fallback_v6`, with matching session/transfer and SHA-256 `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`. The three-copy publication hold closed within its existing bound. The original primary ran three evaluations and one native read. All ten original reports remained byte-identical; full/compact snapshot evidence and parity passed. The original `FAIL_PROTOCOL_OR_INTEGRITY` verdict and failed route gate remain unchanged.

## First failed edge

The resolver accepted 27 witnesses after **3,082,449 visits / 19,284.3814 ms**. The corrected probe then recorded:

| Field | Before first ownership check | After check |
| --- | ---: | ---: |
| Evaluation visits | 4,065,264 | **4,194,305** |
| Cumulative work, ms | 19,439.8281 | **19,456.0739** |
| Custody closed | false | true |

The pre-check row was `FullIndex=44`, `CompactIndex=16`, `SourceLine=122`. The predicate rejected without propagating an exception; the first close caller was `CheckCurrent`.

In `LocalFrontierPrimaryEvaluations.cs`, `VisitEvaluation` increments the visit counter and throws above the cap. `CheckCurrent` catches failure, returns false, and closes custody. The exact cap-plus-one value, sub-limit work time, and open-to-closed transition confirm this path. This trace does **not** establish a corrupt source line or an origin mismatch. `SamePairJoined=false` and `OriginalOutputCommitted=false`; transfer/hash success is not whole-path acceptance.

The earlier diagnostic-scope gap is closed: before/after counts, time, closure state, and caller are all populated from the original custody.

## Why the increased cap is still insufficient

Code inspection identifies three whole-state checks after resolver acceptance and before the first probe ownership check: resolver `GetWitnesses`, resolver `IsCurrent`, and probe `GetWitnesses`. Their observed total is 982,815 visits, implying **327,605 visits per unchanged-state check** for this fixture.

The probe preserves per-row ownership and full-origin checks, plus compact-origin checks where applicable. For 27 witnesses, just ownership and full-origin checks imply a lower bound of:

`4,065,264 + (27 × 2 × 327,605) = 21,755,934 visits`.

This is a source-and-counter-derived lower bound, **not a completed execution or a proposed final limit**. Compact-origin checks and subsequent diagnostic preparation/output require additional accounting. The 20-second clock still has only about 544 ms remaining at the observed rejection; increasing visits alone does not establish that the entire path will fit.

## Bounded fix plan / next step

1. Inventory the remaining currentness calls through all 1–64 witnesses, preparation, and output; derive a complete cumulative visit budget for the supported admitted input/state bounds. Keep producer admission separate.
2. Review the cumulative work-time allowance alongside that budget. Select a finite, explicitly justified resource-contract revision; do not choose another number merely above this first failure. Preserve the existing ownership/currentness checks, cumulative counters, fail-closed retirement, original reports, and diagnostic-only authority flags. If the derived resource cost is unacceptable, present that tradeoff before changing the verification design.
3. After that resource decision, add only affected boundary/regression checks, rebuild, and run one fresh targeted offline native attempt. Live testing remains gated on the complete native milestone; the unchanged failed route verdict must not be relabeled as accepted.

Current limits remain evaluation visits **4,194,304**, producer records **2,097,152**, cumulative work **20,000 ms**, and input records **262,144**.

## Cleanup and preservation

Attempt PIDs 49364, 49036, and 23664 are gone; no conflicting native/test processes remain. All native pins are closed and retained payload/witness text is cleared. Both 256 MiB payload files were independently hash-verified through exclusive read handles.

The artifact root contains the original TRX/logs, preflights, scalar RCA, observation summary, cleanup check, preservation audit, final seal, and final verification. Historical evidence is untouched. This report is the only new non-ignored repository file from this phase.
