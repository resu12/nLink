# Canonical witness-state capacity implementation

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

The user approved proceeding after the diagnostic-capacity decision request.
This implements the reviewed split capacity contract; it does not change route
acceptance. It supersedes the pending-decision status in the preceding diagnostic
report without rewriting that historical evidence.

Status: **implemented and verified: 44 tests passed, zero failed or skipped.
No canonical native-256 or live GUI rerun yet.**

## Surgical implementation

Only `LocalFrontierDiagnosticState.cs` changes production behavior. Original
input events now use a typed row array capped by the existing 262,144-record
input ceiling. The generic failure/proof/view metadata remains capped at 65,536
cells. This separation is the explicitly approved resource-contract change.

Each event row retains six references: original event, original PowerShell owner,
original RawLine and FilePath note properties, original raw-line value, and the
validated path scalar. No event is filtered, deduplicated, replaced by a hash, or
limited to the later witness selection. The original AllEvents note-property and
array identity remain in the metadata guard; every array slot is checked for
original reference and order.

RawLine remains reference-equal, not merely text-equal. FilePath preserves the
existing scalar type/value comparison and shared text accounting. Replacing
either note property is rejected even if its value matches. Script properties
are rejected without executing their getters; hostile replacement Equals methods
are not invoked. Existing metadata/property checks are unchanged.

Every event capture and successful recheck still charges three visits. The
original per-sequence visit is retained. The original 2,097,152 cumulative-visit
limit, 20,000-ms active-work limit, scalar/text limits, 1–64 witness selection,
native owner/pair checks, one-shot rules and cleanup remain unchanged. No runtime
coordinator, retry, route, transport, transfer geometry or deadline was modified.

## Regression evidence

Before implementation, the updated regression failed at 21,826 events with
`frontier_diagnostic_state_capacity`. After implementation, all four historical
sizes (21,825, 21,826, 65,536 and 73,885) capture and revalidate successfully.

Six focused .NET cases passed on Windows PowerShell and PowerShell 7:

- Original hostile-property and metadata controls, the four historical sizes,
  and ten event/array/order/property replacement checks.
- New input, metadata, text, cumulative-visit and active-work boundary controls.
- Real attempt-profile compilation with the previously added scalar observer.

The new controls use the actual `VisitEvaluation` method on an inert test-only
budget receiver. They do not create native custody or evaluate authority.
Successful accounting for minimal synthetic metadata is:

| Event rows | Metadata cells | Capture visits | One validation's visits |
| ---: | ---: | ---: | ---: |
| 0 | 61 | 61 | 69 |
| 1 | 61 | 64 | 72 |
| 73,885 | 61 | 221,716 | 221,724 |
| 262,144 | 61 | 786,493 | 786,501 |

A second maximum-size validation is rejected by the unchanged cumulative visit
limit. Exact 65,536 metadata cells, 2,097,152 visits and 20,000 ms boundaries are
accepted; their next increment is rejected. Work exhaustion before revalidation
still rejects the state. Input overflow, non-zero array lower bound, count
mismatch, null event, executable event properties, invalid scalar and oversized
path text are rejected. The existing rank and metadata-array limits remain tested.

The size/count tests use a controlled stopwatch; they are not native-work timing
or throughput claims. In particular, admitting 73,885 rows does not prove that
the full native witness lifecycle fits its cumulative time/visit budget.

The affected `DiagnosticPreparation_BindsNativeInputsAndRechecksOriginalState`
contract lane ran once after stabilization: **38 passed**, zero failed/skipped,
in 7 minutes 52 seconds. Its small local custody fixtures cover copied identities,
pre/post failure/view/proof/index changes, script properties, replay, disposal,
parent cancellation, work/record budgets and physical-only evidence. These are
not the canonical 256 MiB transfer or live GUI/NKN scenarios.

Total: **44 distinct .NET cases passed** (6 focused + 38 affected contract).
No aggregate analyzer, generic Smoke or full GUI lane was run. The build used
command-local `UsedAvaloniaProducts` telemetry opt-out; its warnings are outside
the changed files. The scoped whitespace check and new PowerShell parse passed.

## Next gate

Rebuild and run exactly one
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`
with fresh frozen inputs and the original strict preflights and deadlines. Use
the new first-rejection observation if another edge is exposed; do not change
work/visit limits or rerun the same capture to force a pass.

The prior native fixture is still failed with `native_frontier_unavailable` and
no same-pair join. This implementation does not retroactively prove its hidden
first rejection or turn its failed route verdict into success. The intentional
late-publication `FAIL_PROTOCOL_OR_INTEGRITY` verdict and strict route-cycle
acceptance remain required. Full GUI/live-256 testing is a later gate.

## Preservation

Entry verification matched 4,750 prior pins and 583 dirty entries. Expected
changes are the diagnostic-state implementation, its existing regression script,
the new capacity-control script, one new test method, rebuilt Core test DLL, this
report and new test artifacts. The audit verified all 4,750 prior pins and all
pre-existing dirty entries, allowing only the expected source/test changes and
rebuilt test DLL. The current dirty count is 587. No commit, push or deletion was
performed.

Independent post-test process inspection found no remaining project test process.
The restricted sandbox denied that read-only inspection; the check succeeded
outside it. No app/network failure or runtime change was inferred from the denial.

Artifacts: `artifacts/canonical-witness-state-capacity-20260929/`.
