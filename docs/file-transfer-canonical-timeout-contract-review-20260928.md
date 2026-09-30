# Offline canonical: timeout and diagnostic-retention contract review

Date: 2026-09-28. Status: review complete; proposed fixture-only changes await
approval. No implementation, build, test rerun, timeout change, or acceptance
change in this step. No live GUI/NKN, portable build, matrix, commit, or push.

## Evidence and scope

The latest rebuilt offline canonical remains
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-3ccd1e52b79c4d4ba079ac8b052f13b6`.
It failed when the outer PowerShell harness killed its process tree after 300
seconds. The prior implementation report is
`docs/file-transfer-primary-read-validation-20260928.md`.

The prior seal matches SHA-256
`3e94386aafd0493728d43274967327de782478479b78ce0d4c8347eb159b2a50`.
This review rechecked 498 pinned files: all pinned non-artifact inputs, the latest
canonical's retained files, and its observation record. All match. The 444-entry
starting dirty inventory also matches the prior checkpoint. Older unrelated
artifact trees were not rehashed for this documentation-only review.

## Deadline composition

| Limit | Current owner and meaning | Proposed change |
| --- | --- | --- |
| 300 seconds | `RunPowerShellScriptTextAsync`: entire offline fixture, including GUI and analyzer | 600 seconds for this canonical call only |
| 210 seconds, plus 120 seconds finalization | Original GUI scenario; initial hard deadline 330 seconds | None |
| Up to 540 seconds | Existing maximum GUI deadline with its separately validated terminal-phase mechanism | None |
| 1,800 seconds | RouteAcceptance retained-analyzer wall-clock deadline | None |
| 20,000 ms | Native source's cumulative active-work budget | None |
| 5 seconds per output task | Outer helper's existing timeout-path output drain, after kill | None |

The canonical caller currently omits the helper's existing optional `timeout`
argument. The shared five-minute default is therefore applied to the whole
multi-process workflow, not just analysis. The latest GUI child consumed about
105.45 seconds; the analyzer subsequently reached source/origin completion and
coordinator reports. Efficiency reporting started at 05:20:58.3652664 UTC;
the enclosing test finished after timeout at 05:21:01.4292661 UTC. These stamps
do not establish the exact kill instant or how much unfinished work remained.

The proposed 600-second value is a deliberately bounded diagnostic retry
envelope, not a measured completion guarantee or transfer-performance target.
It provides another 300 seconds to this one fixture while retaining the shared
default. It can still preempt an analyzer that is within its own 1,800-second
limit: nominal GUI maximum plus analyzer budgets alone total 2,340 seconds,
before setup and finalization overhead. We do not recommend expanding to that
entire envelope based on one interrupted run.

The helper initiates termination at the selected timeout; process termination
and its existing output-drain waits occur afterward. Thus 600 seconds is not an
absolute bound on the complete test method's return time. No adaptive extension,
automatic retry, or second increase is proposed. Another timeout remains failure.

## Why output was unavailable

The outer helper owns only its PowerShell parent's redirected streams. It kills
the entire process tree on timeout, so draining those streams cannot recover
data buffered inside the analyzer's parent or another descendant.

`LocalFrontierPrimaryReports.ReadReport` consumes the original primary pipe and
tees bytes into an ordinary `FileStream`. `ReportTee.ReadAsync` explicitly flushes
the file on EOF; capture sealing and disposal follow complete reading. The outer
kill can prevent that completion. The observed empty raw files therefore do not
prove an absence of emitted primary diagnostics. This is a code-backed possible
loss mechanism, not a reconstruction of the missing bytes.

RouteAcceptance writes the normal retained stdout/stderr files and attempt
receipt after primary exit. Those files were absent in this run. Do not create
replacement receipts, mark EOF, infer exit success, or infer the final evaluation
count from the partial artifacts.

### Proposed test-only diagnostic checkpoints

Retain selected existing diagnostic values directly from the primary fixture,
independently of the normal redirected-output completion path. Do not change
production output pumps, add a second reader, replace `Console.Out`, or introduce
PowerShell transcript/global stream redirection.

- Activate only for the exact `canonical-fallback` fixture profile. Use a distinct
  `test-primary-checkpoints.ndjson` file under its unique artifact directory,
  never a normal report, source, receipt, or recovery-custody filename.
- Bound it to 16 records, each at most 8 KiB of encoded UTF-8 including its
  terminating newline: at most 128 KiB total. Use fixed milestone names and
  record schema, PID, attempt/scenario identity, sequence, timestamp, and the
  existing diagnostic text. This is selected diagnostics, not a full output log.
- Capture the original source/origin counts and already-produced work-profile
  string, binding outcome, cleanup outcome, original first error, and terminal
  fixture outcome when those points are actually reached. Build each diagnostic
  value once and reuse it for the existing output and checkpoint. Do not repeat
  currentness checks, binding, source reads, or coordinator evaluations for logging.
- Write/flush/close at each milestone so earlier completed records do not depend
  on a future EOF. Treat a truncated final record as unavailable; retain only
  complete bounded records. This is not a power-loss durability guarantee.
- Diagnostic write failure must not replace the original exception, change its
  exit outcome, skip cleanup, or promote incomplete proof. Missing records mean
  unknown. A finish record is written only after the existing original assertions
  succeed; a source, bind, or cleanup record alone is not final success.
- Preserve all original timing charge points and checks. Do not move native work
  outside its clock or subtract diagnostic overhead from any budget.

Keep this implementation in test support and its focused tests. A small fixture
helper is sufficient; no new production token, observer protocol, generic stream
framework, or watchdog service is needed.

## Downstream mode assertion: distinct issue, not this timeout's cause

`Support/FrontierRelayAnalysis.ps1:89` currently requires `LegacyV0` for every
primary-payload case except `canonical-source`. That includes `canonical-fallback`.
The latest full and compact reports both contain `SnapshotV1Authoritative`,
`StreamComplete=true`, and `ArtifactEvidenceVerdict=pass`; parity is `pass`.
The receipt writer copies its mode directly from the full summary.

If the current artifacts survive normal finalization, the existing LegacyV0
assertion would reject that receipt. No receipt exists yet, so this remains a
source/artifact contract mismatch, not an observed terminal assertion failure.
The LegacyV0 expectation was appropriate to the earlier incomplete toy source
described in `docs/file-transfer-payload-primary-handoff-20260927.md`; it is not
equivalent to route-proof incompleteness for an authoritative canonical stream.

Before spending another full canonical run, separately approve this narrow
fixture distinction as part of the implementation contract:

| Fixture | Required receipt mode | Required proof outcome |
| --- | --- | --- |
| `canonical-source` | Existing `SnapshotV1Authoritative` | Existing completed outcome; unchanged |
| `canonical-fallback` | Explicit `SnapshotV1Authoritative`, with full/compact stream evidence and parity intact | Still `proof_incomplete`, `completedProofArtifacts=false`, and the original incomplete-proof rejection |
| Other existing primary-payload cases | Existing `LegacyV0` expectation | Existing outcomes; unchanged |

Do not accept either mode opportunistically, derive an allowed mode from the
observed output, or remove the incomplete-proof assertion. The canonical fallback
case must still retain its original three evaluations, one native source read,
input before/after checks, identity/count/hash checks, and custody cleanup.

The current operator report is INCONCLUSIVE with
`filetransfer_gui_live_reactivation_action_window_missed`. Authoritative snapshot
parity does not establish a second coordinator-published `file_tuna_v4` route.
Neither this mode distinction nor the deadline proposal changes live acceptance.

## Implementation and verification plan, if approved

1. Add bounded test-only checkpoint emission at the original diagnostic sites.
   Verify complete-prefix survival under a short forced process-tree timeout,
   record bounds/truncation, write failure, unchanged original stdout/stderr,
   preserved first error, cleanup, and no repeated predicate/evaluator calls.
2. Supply `TimeSpan.FromMinutes(10)` only at
   `CanonicalFallback_RequiresOriginalHandoffsBeyondOldOriginCap` using the existing
   helper parameter. Verify the shared 300-second default and native/GUI/analyzer
   limits remain unchanged. Exercise termination with a short controlled timeout,
   not a ten-minute sleeping test.
3. Split only the canonical-fallback receipt-mode expectation as reviewed above.
   Focused cases must reject wrong mode, missing/failed parity or stream evidence,
   missing receipt, completed-proof promotion, and loss of the original rejection;
   existing toy and canonical-source expectations must remain unchanged.
4. Rerun directly affected fixture/error-preservation controls, rebuild the smoke
   assembly, then run this single offline canonical once. Do not rerun the already
   green native-read suite, unrelated aggregates, or a live matrix for these
   fixture-only edits unless their covered behavior changes.
5. Preserve the new result, actual output/receipt if produced, checkpoints, and
   cleanup observations. On failure, identify the next observed edge and stop;
   no automatic budget increase, re-evaluation, or route-proof relaxation.

The three proposed changes are not applied by this review. Approval should cover
the canonical-only ten-minute envelope, diagnostic-only checkpoints, and the
strict canonical-fallback mode split before implementation and its one retry.
