# Native terminal selection implementation and verification

2026-10-02. The approved terminal-only offline probe is implemented. Twenty-five
focused and compatibility controls pass, and one fresh terminal scale probe
completes both full scans and returns two witnesses at 114,819 cumulative
milliseconds. This completes the narrow size/custody diagnostic, not shared
integration or live acceptance. The original strict scale gate remains failed
at `content_input_budget` under its unchanged contract.

## Implemented scope

The [loader](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1)
adds an explicit `TerminalSelection` option and a separate compilation symbol.
It rejects combination with source-read profiling. The
[terminal partial](../tests/nLink.SmokeTests.Core/Support/NativeContentTerminalSelection.cs)
provides a fixed 150,000-ms cumulative allowance; it does not expose a general
caller-configurable limit. The ordinary producer and evaluation constants
remain 20,000 and 60,000 ms.

The terminal wrapper reuses the original acquisition implementation. Its
scoped cursor branch retains original resource checks, full byte comparisons,
both full scans, array/view/event identity, original physical origins, ordered
1–64 witnesses, selected-event inclusion and all count/code-unit ceilings.
Terminal visits increment the same native evaluation counter and enforce its
original count ceiling, using the explicit terminal time guard. Ordinary
visits still invoke the original 60-second evaluation path. No native clock
is reset or replaced in the scale probe.

Terminal entry rejects a declared remaining consumer, an earlier content
attempt or used payload/witness/resolution slots. The final-consumer argument
is a test-harness declaration, not a native terminal capability. Its validity
depends on the fixed
[fixture script](../tests/nLink.SmokeTests.Core/Support/NativeContentTerminalSelection.ps1)
and caller, not on a flag alone. The script completes original `Owns` and
physical-origin oracle checks before entry, under their original guards.
Their elapsed work and visits remain charged. The oracle is an independent
test expectation, not authority for accepting a later row.

After entry, the fixture performs only detached-result validation,
ended-cursor rejection and passive bookkeeping, then waits for normal
release. The [worker](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionWorker.ps1)
returns directly from this branch to its existing final disposal. It does
not perform post-selection `Owns`, origin lookup or another budget-bearing
shared-custody operation. Neither optional failure nor the terminal wrapper
explicitly closes inputs to conceal time exhaustion. Normal final disposal
is cleanup, not proof that future shared-custody consumers remain usable.

Only exact `content-native-terminal-scale` receives a 180,000-ms worker
attachment deadline and 210,000-ms outer harness wait. The outer wait permits
the inner deadline to be observed; neither supplies active work allowance.
Existing scale/profile waits remain 90,000/120,000 ms and ordinary waits
remain 30,000/20,000 ms. The existing strict scale assertion is unchanged.
The new case reuses the original large fixture, processing and copy paths.

## Focused verification

The [focused controls](../tests/nLink.SmokeTests.Core/NativeContentTerminalSelectionTests.cs)
cover explicit loading on PowerShell 5.1 and 7, terminal call order, exact
deadline selection, successful selection, inherited elapsed work, time,
record and code-unit exhaustion, copied arrays/views/events, mutation,
declared remaining consumers, used frontier slots, excess witnesses,
selected-event omission and stale ownership. They also check detached rows,
ended cursors, inactive/stopped return state and unchanged frontier slots.
The synthetic 70-second elapsed control succeeds under the terminal guard;
the 151-second control rejects without closing inputs. These injections are
confined to small controls and are not used in the scale run.

Ordinary positive acquisition on both hosts, ordinary time exhaustion and
mutation rejection, plus existing wait-selection controls, still pass.
Ordinary fixtures retain their post-selection `Owns` and origin checks.
There are 25 distinct passing focused/compatibility controls, separate from
the single scale test. The two loader controls first fail as expected before
implementation. An early call-order verification used a stale test assembly
containing an old quoted script; rebuilding the corrected test assembly
resolves it. That failure is not runtime RCA evidence.

Reconstructing the ordinary adapter by removing terminal branches and
inlining its unchanged 20-second local value reproduces its previous source
hash exactly. Normal Core/Infra binaries are byte-identical, and test-only
native custody types are absent from all three inspected normal assemblies.
The normal test assembly is rebuilt for the new controls. One build attempt
fails in the sandboxed Avalonia telemetry-write target; the affected test
project then builds with that target disabled and existing dependencies.
No application source changes are made for that build failure.

## Fresh terminal scale result

Selection enters with 18.3378403 seconds already charged and 919,969 native
evaluation visits, including the added pre-entry oracle/currentness work.
Selection charges 96.4820788 seconds, ending at 114.8199191 seconds with
1,124,413 visits and 240,627,592 charged code units. There are 102,218 source
rows, 204,436 completed full-row visits across two scans, 817,769 fresh
resource checks, one primary selector callback and two returned witnesses.

The full scans take 96.4741590 seconds; resource checks account for
96.1045344 seconds within them. These intervals overlap and must not be
added. The original work clock is stopped at entry and return; inputs remain
open and the scoped terminal flag is inactive at return. These are passive
fixture observations, not proof of post-return shared-custody usability.

Earlier processing succeeds with a 5,512-ms parse and 7,850-ms cumulative
processing work. The 208,326,025-byte semantic copy succeeds in 2,741 ms,
with 10,595-ms cumulative parent work. Persisted semantic and analyzer copies
match in actual bytes and SHA-256. Both worker streams are complete and
untruncated; stdout retains 4,800 characters and stderr is empty. The parent
reports completion without reaching the 180-second attachment deadline.
The whole test lasts approximately 3 minutes 41 seconds, including fixture
setup; that duration is not the native cumulative-work measurement.

## Remaining integration work

The [approved review](file-transfer-native-selection-allowance-contract-review-20261002.md)
explicitly excludes later shared-custody consumers. The terminal method and
fixture must not be installed as a general production integration path.
Raising only selection still leaves original 20/60-second consumer guards;
calling them after this run can close shared inputs. The original scale gate
has not been rerun or relabeled as passed.

Recommend using the complete scan measurements to prepare a production-safe
integration fix plan. It must account for remaining custody consumers and
preserve fresh validation, inherited work and rejection semantics; it must
not widen shared guards or treat the terminal declaration as native authority.
Content-family coverage, reported-candidate binding and production integration
remain incomplete. No new live-256 test or full GUI matrix is warranted yet.

The [verification receipt](../artifacts/native-selection-terminal-20261002/verification.json)
pins the focused results, current terminal artifacts and 91-file launch
checkpoint, checks earlier strict scale evidence and preserves unrelated
dirty files. No production-tool path changes in this step. No aggregate
suite, normal scale rerun, portable package, live GUI/NKN run, commit or push
occurs. Runtime authority and acceptance remain unchanged.
