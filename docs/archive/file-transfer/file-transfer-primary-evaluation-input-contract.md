# C1j: private primary input/evaluation association

Date: 2026-09-14. Base HEAD `ef78e9d7`, with accumulated uncommitted work.
This implements the compact primary's invocation-local input/view association.
It does not complete writer membership, selected A/B1 preparation or V2 issuance.

## Ownership and integration

C1i can convey an accepted GUI context to the retained worker, but a native hash
or caller-supplied completed view cannot identify an actual primary invocation.
The compact writer now retains the exact two `string[]` assembly outputs around
its original two evaluator calls. The original order, catalog predicate, raw
strings, duplicate handling and evaluator call sites are preserved. Both arrays
are assembled before the first call so optional preparation can precede evaluation.
No new authority evaluation is added. The full-report path is unchanged.

The outer primary forwards optional ownership only for one source, TailMinutes
zero, no appended synthetic events, a completed source read and an accepted GUI
writer context. `Worker.OwnsCompletedSource` checks the actual reader reference
against that worker's private reader and its current lifetime. A matching path,
duck-typed reader, detached JSON or successful-looking boolean is insufficient.
Incompatible previously loaded owner types omit without changing normal analysis.
No live parent enables these prerequisites yet.

`LocalPrimaryEvaluationInputs.ps1` imports only C1c's capture loader. It does not
load the diagnostic consumer, redefine the primary evaluator, inspect saved
summaries, read files or verify the writer candidate. During preparation it checks
the actual index's source path, strictly increasing physical lines, integer parser
sequence equality, original raw-string references and exact compact catalog
subsequence. Full positions and physical source lines remain separate arrays.

C1c fingerprints the four roles using the reviewed framed UTF-16 domain. Full
capture, full Snapshot and compact capture currently use the same all-event
array; compact Snapshot uses the independently selected reduced array. Their role
descriptors remain separate. No file path is invented for a native array.

The writer keeps the result in `private:localPrimaryInputs`. Immediately after
the original evaluations, association checks exact array identity, every immutable
string reference against bounded shallow snapshots, the still-owned native token
and current source lifetime. An evaluator-side array mutation, substituted array,
missing returned view, cancellation or replay discards the entire optional context.
It retains the exact returned full/reduced objects without copying private views.
An evaluation exception propagates normally; no association or retry evaluation
runs. A `finally` invalidates the native token and drops all array/view/source
references on success and failure, including primary report failures.

## Resource bounds

Let F and C be full and compact native record counts. Admission requires C <= F
and `7*F + 4*C <= 262144` before inspecting any event or allocating optional
per-record arrays. This conservatively counts the event/origin pass, origin
writes, four native-role visits, shallow snapshot copies and post-call reference
checks. It is not a count of every property access, allocation zeroing operation
or kernel lifetime poll. Those remain proportional to admitted records.

Additional per-record storage is two integer origin arrays plus two shallow
string-reference arrays: approximately `12*(F+C)` bytes on a 64-bit host, excluding
array/object headers. Strings and completed views are not copied. C1c retains its
4-KiB hash buffer and four small immutable descriptors, 262,144-unit per-line
limit, 67,108,864 total hashed code units and 250-ms active API work ceiling.
The three equal full roles currently incur three hashes; no zero-cost alias is
claimed. Length framing and Unicode behavior remain C1c's unchanged contract.

Preparation and post-call validation share a cooperative 2,000-ms elapsed work
budget with per-record source/cancellation checks. The clock stops across the
existing authority evaluations. Optional module compilation occurs earlier,
before preparation/evaluation, and is not included in that clock. Neither budget
is a hard scheduling deadline or proof of capacity for live-scale inputs. Existing
parent watchdogs are unchanged. Over-cap inputs omit instead of enlarging limits.

All new traversals, hashing and shallow copies occur before primary reports.
The final path only clears a fixed number of references and disposes the bounded
native owner; it starts no serializer, scan, file read, wait, IPC or background work.

## Deliberately not a completion capability

The PowerShell preparation object is mutable and private to trusted orchestration;
it is not an authenticated public DTO or a completion issuer. Direct helper calls
with fabricated index/view objects do not prove an actual evaluator invocation.
The production call site supplies that association. `Associated` describes this
point-in-time association, not passing Snapshot proofs or continuing eligibility;
later consumers must recheck owner lifetime. No formatter receives the context.

Writer-context presence is historical GUI provenance, not verification of current
writer bytes. C1i's bounded `TryOpenWriter` traversal still needs integration with
exact selected source/writer membership. The approved first-failure witness and
independent full/reduced A/B1 rows still need binding under this same private
primary lifetime before reports. The issuer, dynamic expectation channel,
Wrapper-to-RouteAcceptance process relay and post-receipt adoption remain separate
gates. C1a's pre-known-digest slot stays disconnected; live export remains disabled.

## Verification

The new focused tests use an actual accepted GUI finalizer completion and exact
launched retained worker on PS5.1/PS7. They exercise valid association and malformed,
forged, incomplete, over-cap, changed, cancelled, repeated and over-budget cases.
The record-cap test uses a throwing property to prove rejection before traversal.
Control views are explicitly diagnostic sentinels, not publication proof fixtures.

Five Windows PowerShell primary attempts exercise baseline, owned inputs, later
report failure, mutation after the real evaluator and evaluator failure. The
synthetic LegacyV0 fixture keeps its original three authority calls (one before
the injected evaluator exception), report bytes except the two existing timing
fields, private-scope isolation and cleanup. PS7's unrelated pre-existing
`IsWindows` stage-writer collision is not patched or hidden.

Related input-contract, primary-witness and generated service/transport/logger
regressions retain exact inputs and independent forwarding proofs with two
original Snapshot evaluations on each host. These generated tests validate the
changed input-assembly seam, not live capacity or the still-pending bound writer
and selected-row integration. Results: `artifacts/publication-primary-inputs-20260914/RESULTS.md`.

Follow-up: [C1k](file-transfer-primary-publication-witness-contract.md) connects
verified writer membership and independent selected A/B1 rows before reports,
using this same private context and optional-work clock. It remains preparation,
not completion issuance or live export.
