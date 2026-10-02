# Native adoption budget increase and verification

2026-10-01. The approved shared active-work cap is now 15,000 ms. The owned
cursor parser retains a separate 10,000 ms local allowance; generic readers,
generic cursor readers, and owned non-cursor readers retain 5,000 ms. The
original semantic-copy gate passes in the targeted normal-path scale fixture.
The overall fixture remains incomplete because a later worker signaling
barrier times out.

## Implementation scope

The production change is two lines: increase
`LocalFrontierFileAdoption.MaximumActiveMilliseconds` from 10,000 to 15,000,
and pin the owned cursor parser's local allowance to 10,000 rather than
deriving it from that shared constant. Reversing these two edits in memory
reproduces the exact pre-step hashes of both production files.

The cap applies to the original shared clock, including ledger creation,
adoption, processing, copying, and later work charged to that clock. No clock
is reset or replaced. Managed waits, validation cadence, cancellation,
fresh file identity checks, copy chunk size, hash/line/byte validation,
handle ownership, and failure retirement are unchanged. Producer, evaluation,
record, byte, and worker-wait limits are not increased. Route authority and
live acceptance rules are unchanged.

Deadline fixtures now simulate shared expiry at sixteen seconds rather than
eleven. The local parser still rejects at eleven seconds. Scale diagnostics
report the new shared cap and the unchanged local cap. A fixture-only record
captures the original semantic-copy return value and immutable output byte
count; it neither calls another custody check nor issues authority.

## Focused test results

Twenty focused tests pass. They verify shared-clock identity and accounting,
acceptance at eleven and fourteen seconds, rejection above fifteen seconds,
continued local-parser rejection above ten seconds, and generic rejection
above five seconds. Original copy controls succeed after advancing shared
work to fourteen seconds and still return no owned copy after expiry at
sixteen seconds, even when all small-fixture bytes were already written.

The affected deadline and copy-custody lane initially records 92 passes and
two failures in the extracted GUI-startup fixture. An isolated diagnostic
rerun confirms that the real cleanup checks `$guiCanonicalAttempt`, while the
fixture never initializes it. Initializing that fixture variable to null fixes
the stale setup without changing production cleanup or weakening its
assertion. Ten corrective controls pass, including both failed deadline cases,
normal startup, and the intended cleanup exception.

There are 122 unique passing focused and regression cases after the repair,
with no unresolved regression failures. The original failed runs remain
preserved. No aggregate contract suite or full GUI matrix is run. The normal
Core, Infra, and test assemblies rebuild and are repinned; their byte identity
with the preceding build is not claimed.

## Targeted normal scale result

One normal-path scale fixture runs. An earlier filter-selection attempt
executes zero tests and is preserved separately, not counted as a scale run.
The actual fixture reports `ProductionProcessingPath=true` and `Processed=true`.
The whole processing call takes 5,292 ms, with cumulative active work at
7,345 ms afterward. This is not isolated parser CPU time or live throughput.

The original `TryCopySemanticEvidence` call returns an owned copy containing
all 208,326,025 bytes at 9,847 ms cumulative work. The persisted original
evidence lists matching semantic and analyzer-output byte counts, line counts,
and SHA-256 values. Verification also checks their actual file hashes. These
observations establish that the copy gate passed in this run; they do not
establish continuing custody after later failure or full content acquisition.

The fixture subsequently fails with `Native worker attachment deadline` at
the existing 30,000 ms signaling wait. The worker has not exited at that
decision, as established by the original failure branch. The signal is set
after source reading, original evaluations, and content-input selection, or
in the worker's final cleanup. Therefore this is a missing worker-completion
signal, not proof that the native attachment call itself timed out.

The first unfinished worker operation and its underlying rejection or cost
are not localized. In-memory worker stdout/stderr are not saved by this
failure path, so this artifact cannot establish whether content selection
started. Do not infer another exhausted work cap or increase the worker wait
from the headline error alone. The single targeted scale test remains failed.

## Checkpoint and next diagnostic step

The [verification receipt](../artifacts/native-adoption-budget-increase-20261001/verification.json)
pins the implementation, test results, and fresh scale artifacts. Unrelated
accumulated changes, earlier receipts, both preceding scale artifact sets,
and all 99 historical live artifact pins remain unchanged. No portable build,
live GUI/NKN run, commit, or push occurs in this step.

Next: add bounded fixture-only stage records immediately around existing
worker attachment, source reading, evaluation, and content-selection calls,
and retain worker output on the original timeout cleanup path. Do not add
another authority evaluation, change synchronization, or extend the 30-second
barrier. Verify output capture and original failure retirement with small
controls, then run one targeted scale diagnostic to identify the first
unfinished operation before proposing another runtime change.

Production integration remains incomplete. Native input-identity fixture
progress is not content-family completeness, reported-candidate binding,
coordinator-issued route proof, or live acceptance.
