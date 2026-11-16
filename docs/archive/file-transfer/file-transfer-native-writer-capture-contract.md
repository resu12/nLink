# C1g: immutable native writer-output provenance

Date: 2026-09-14. Base HEAD: `ef78e9d7`, with the accumulated uncommitted local
publication evidence work. This is an opt-in native output capture and a tested
finalizer output seam. It does **not** close trusted finalizer completion,
GUI/retained-worker binding, evaluator ownership or V2 publication completion.

## First missing link

The existing native ledger processor produces the writer, retained and semantic
files in one write/hash pass. Its public result, writer array and nested writer
rows are mutable. Their copied hashes and validation counters cannot later prove
that a candidate came from that invocation. Separately, the PowerShell finalizer
checks process-output markers, deferred recovery, drains, cursor integrity and
capture/copy consistency. Native sequence/chronology success does not replace
those later checks.

`ProcessWithNativeCapture` now executes the same processing core as the existing
`Process` entry point and optionally retains an immutable provenance object. The
original `Process` signature and default behavior are unchanged. No existing live
caller requests the new capture.

The capture copies the original source path, frozen line boundary/bookmark,
acceptance/scenario IDs and bounded, immutable writer summaries. Each of the
original retained/writer/optional semantic outputs has its creation path,
SHA-256, decoded line count, byte count and volume/file identity captured from the
actual output handle after the original hash and flush, before that handle closes.
The capture adds no output hash pass, reread, source scan or file copy.

The source boundary is the processor's requested range and actual visit count,
not a new claim that the source spool reached EOF or remained quiescent. Its
source pathname is not an independently verified source-file digest. The actual
finalizer's original writer-stop/drain and cursor checks remain necessary.

`CreationPath` is deliberately not a promise about a current pathname. The normal
finalizer renames its temporary files afterward. No write-access handle is kept,
no sharing mode is weakened, and no new handle blocks ordinary primary readers.
Current writer bytes still require independent verification during their admitted
evidence traversal, such as C1e's `OpenExpected`/`VerifyAndReadLines`. Matching file
identity and size alone cannot prove that bytes were not changed after the
processor closed its writer. The tests demonstrate this distinction explicitly.

## Ownership and bounds

`NativeCapture` has no public setter or constructor. Its output records and
writer records have no public setters/constructors, and its writer collection is
read-only. `OwnsNativeCapture` checks the exact capture and an opaque identity
belonging to that result. A new/copy result or matching checksum does not acquire
ownership. Mutating the original public result or its writer rows does not mutate
the captured facts.

The capture retains only its small opaque identity, not the original mutable
result. In particular, it does not keep the result's potentially large drain or
deferred-batch arrays alive. There is no public deserialize, arbitrary digest
setter, or caller-authored success-verdict factory.

Admission limits: at most 64 writers, 1,024 code units per writer/run/scenario
field, 4,096 code units per path, 16,384 total retained metadata code units and a
250 ms cooperative elapsed preparation budget. Preparation uses at most three
output identity/length checks and one pass over the admitted writer metadata.
It does not revisit source lines or batch/drain arrays. The clock starts at the
output identity capture and includes the intervening existing result assembly;
a slow original assembly can conservatively omit the optional capture.

Original byte/line counts are recorded as nonnegative scalars. Recording a large
output descriptor does not admit its bytes to a later consumer: C1e/C1f and C1b's
own input/verification limits still apply. Metadata capture is independent of
the original source's length; it is not another full-input preparation pass.

Native writer gaps, duplicate sequences, identity mismatch, invalid batch
chronology, chronology inversions/collisions, invalid scope, over-cap metadata or
budget exhaustion omit the whole capture. Native processing errors retain their
existing failure behavior. Missing optional native capture never changes the
original processor result or primary output files.

## Actual finalizer seam

`Write-FileTransferGuiLogLedgerEvidence` accepts an optional empty
`LocalNativeWriterCapture` reference. Only this request selects the new native
entry point. A preloaded older processor without that entry point continues
through the original `Process` method and provides no optional capture.

The actual finalizer hands back the native capture only after its original
writer/cursor integrity checks pass, the ledger JSON is persisted and the
original finalization stage update succeeds. Missing drain/output markers,
unresolved recovery, cursor failure or later primary failure withhold the output.
Assigned references are untouched; incompatible typed references omit the
optional result. Rolling-memory processing has no native capture to return.
No existing live call site passes this reference.

This return point establishes a place for the eventual private finalizer owner
to receive immutable native facts. **The capture's type itself does not attest
that the whole finalizer ran:** it can also be produced by the native processor
directly. It must not be accepted as a full writer-validation receipt. No caller
can turn its hashes or presence into coordinator, route, runtime or publication
authority. Diagnostic JSON and direct-evidence manifests remain evidence only.

## Verification and review corrections

Seven new focused tests cover both PowerShell hosts, the actual finalizer and
generated service evidence. Native controls exercise 28 original processing
passes per host, comparing primary scalar results and independently checking
captured output bytes/identity after rename. They cover scope/native-integrity
rejection, 64/65 writers, text/metadata caps, exhausted budget, immutable nested
data, exact-owner rejection, and mutation after original write completion.

The finalizer runs 13 times per host against actual append-only ledger,
snapshot, diagnostic and validation code, without GUI producers. Its four output
files are byte-identical with capture on/off; original ledger JSON is identical
after replacing only the differing output directory. Existing failure verdicts
are preserved. No authority evaluator is called by the capture/finalizer tests.
Two additional attempts per host preload a test-only processor API with the
optional method hidden, verifying normal primary completion and capture omission.

Generated service/transport/logger evidence has 232 source/writer lines, one
writer and eight publication uses. The capture matches independently verified
writer identity and bytes on both hosts; the original two full/reduced authority
evaluations and all eight A/B1 forwarding checks remain unchanged. This generated
native check does not issue a finalizer or process-completion receipt.

Review removed a strong reference from capture to the whole primary result,
which would otherwise retain unbounded existing arrays. It also preserved the
old processor entry point for default and preloaded-type compatibility.

The directly affected scale lane exposed an older stale assertion. Running the
original 200,000-line test with the HEAD processor and HEAD finalizer reproduced
it: retained=200000, semantic=0, analyzer=0. The fixture contains only writer
start/drain and ordinary payload events, so its semantic input is empty. The
test now requires analyzer/semantic equality and zero semantic bytes/lines while
retaining the 200,000-line full/writer checks. No runtime behavior was changed to
make this test pass. Detailed results and reproduction:
`artifacts/publication-native-writer-20260914/RESULTS.md`.

## Next ownership boundary

The opt-in GUI parent/finalizer binding is now implemented separately in
[C1h](file-transfer-local-gui-writer-request-contract.md). The C1g capture itself
still has no finalizer-completion authority. No existing live caller is enabled.

C1h binds the existing GUI finalizer to its actual parent-launched process and
keeps the completion capability private to that finalizer. Its successful
existing checks bind these native facts to the ledger/copy outcome and run
through a parent-owned bounded control channel. Optional formatting must not
receive that issuer, and no factory may accept caller-declared validation
booleans or diagnostic JSON as completion.

Carry that separately accepted writer context through the original Wrapper and
RouteAcceptance lifecycles into [C1f](file-transfer-local-retained-source-request-contract.md).
Then independently verify the candidate writer during the admitted traversal and
associate the primary's actual full/reduced evaluator contexts and failure
witness. The original exit/deadline/terminal-receipt ordering and C1b's omission
for unsupported milestone/synthetic augmentation remain mandatory. No live
256 MiB run, exporter wiring or completion adoption is justified by C1g alone.
