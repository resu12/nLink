# C1c: bounded native-input capture

Date: 2026-09-14. Base HEAD: `ef78e9d7` plus the existing uncommitted evidence work.
Implements the input-identity prerequisite of C1b. No publication-completion
issuer, producer control channel, V2 carrier or production caller is added.

## Implemented boundary

`LocalNativeInputCapture.cs` incrementally fingerprints the exact strings passed
to it using C1b's `nlink-ordered-utf16le-v1` framing. It retains a fixed byte buffer
and four small input descriptors, not source strings, raw input copies or private
Snapshot graphs. It performs no file/network I/O, authority evaluation, process
launch, wait, retry, task dispatch or callback.

One producer owns `BeginInput`, `AppendLine`, `EndInput`, `Take` and `Dispose`.
Only `Cancel` may be called concurrently. Inputs are processed one at a time,
with exact case-sensitive roles `fullCapture`, `fullSnapshot`, `compactCapture`
and `compactSnapshot`. Each role appears once; append ordinals are one-based,
contiguous and bounded by its declared count. Empty arrays/strings are valid
native identities; null lines, wrong roles/order/counts and incomplete inputs
omit the entire capture. The separately frozen writer file/ledger is not supplied
or validated by this class and must not be relabeled as a native role.

After all four inputs complete, `Take` returns one immutable descriptor set with
the supplied request GUID and per-role count, code-unit count and SHA-256. Normal
public callers cannot construct these descriptor types. `Owns` requires the exact
issued object, not an equal request or equal fields. A second take returns null;
cancellation/disposal invalidates ownership, including already returned sets.
No rejected call can revive the owner. A failure in any input prevents a partial
set from being taken. Budget and shape failures remain observable diagnostically.

These are **input fingerprint tokens**, not publication-completion receipts.
The GUID is supplied by the caller; this class does not authenticate a parent or
worker process. It does not prove original file/writer membership, that a current
mutable array still matches captured strings, that an evaluator consumed those
strings, or that any selected row is authoritative. `Owns=true` must never be used
as that proof. Later producer code must bind the exact primary invocation, original
inputs, completed views and A/B1-selected rows before issuing completion.

## Fixed work and storage ceilings

| Resource | Ceiling |
| --- | ---: |
| Record visits across all four roles | 262,144 |
| UTF-16 code-unit visits across all roles | 67,108,864 |
| UTF-16 code units in one line | 262,144 |
| Reusable hash buffer | 4,096 bytes |
| Accumulated time inside capture API calls | 250 ms |

Limits are fixed, not caller-adjustable throughput policy. Declared record limits
and each line's length are checked before consuming that input. Cancellation and
time are checked at API boundaries and every 2,048 code units (4,096 framed bytes).
`RecordVisits`, `CodeUnitVisits` and `FramedByteVisits` account for processed data,
including work before an omitted result. No whole array is serialized or buffered.
Framework SHA state, four descriptors and fixed metadata are additional bounded
storage; the 4 KiB value is not a claim about total process/GC memory.

The 250 ms ceiling is cooperative elapsed time inside C# calls, not a hard CPU or
wall-clock deadline. It excludes caller-loop dispatch, module compilation/loading,
reference verification and time between calls. A bounded operation, OS scheduling
or GC can overrun a checkpoint. `Cancel` is noticed at the next checkpoint; no
claim is made that an already executing hash call is forcibly interrupted. The
actual full producer still needs end-to-end cost/deadline verification. Do not
put a fresh input traversal or Add-Type startup after primary report publication.

The ceilings safely omit unsupported larger inputs; they are not evidence that
a live 256 MiB run will fit them. No performance policy or primary watchdog changes.

## Verification scope

Unit checks on PS5.1/7 cover pinned framing vectors, work accounting, source-array
mutation after capture, same-GUID cross-owner rejection, one-shot behavior,
cancel/dispose, malformed roles/order/counts, incomplete input, null/oversized
lines, total-work saturation and a controlled exhausted active-time budget.

A fresh real-service/fake-NKN encrypted transfer supplies eight publication uses.
The actual retained compact writer's two input-assembly statements invoke the real
full/reduced evaluators, with a test-only capture observer before each call. Exact
native hashes match the independent test reference. Existing A/B1 validation still
matches all eight uses; original source and completed views remain unchanged.
There are exactly two primary evaluations and no additional authority evaluation.

The generated log has 232 physical lines but 231 parsed events. The test preserves
the real parser's source mapping and materializes the exact native arrays only as
test inputs for the file-based A/B1 oracle. It does not rewrite boundary offsets
or use the retained file's physical offsets as native offsets. These test files
are not a proposed production persistence mechanism.

Retained-parent lifecycle coverage uses small fixed native strings and controlled
primary report fixtures. Successful capture, exhausted-budget omission and
cancellation preserve primary outcomes and terminal receipts. The completion
payload remains C1a's constant opaque fixture, not a V2 evidence carrier. Tests
load the module before primary report publication. Neither startup near a deadline
nor real selected-row preparation is established by that lifecycle fixture.

## Next binding remains required

Follow-up: [C1d diagnostic native-input binding](file-transfer-local-native-input-binding-contract.md)
now verifies source/writer membership and selected full/reduced rows over supplied
objects. It explicitly does not verify frozen-file or evaluator-invocation
ownership and cannot issue completion. Its whole-input reference traversal remains
disconnected from production finalization.

The native fingerprints now have an implemented bounded producer mechanism, but
the original source/writer membership and exact selected-row lookup still must be
bound to their actual completed full/reduced views. Only that primary-owned context
may issue the separate C1b completion record. Do not issue it from these tokens,
from a payload checksum, or from diagnostic route caches alone. Keep production
wiring off until that row/input association and its complete lifecycle pass.

Results: `artifacts/publication-native-preparation-20260914/RESULTS.md`.
No new live 256 MiB test is required for this slice.
