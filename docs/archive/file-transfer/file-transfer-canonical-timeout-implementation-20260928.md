# Offline canonical timeout implementation and receipt-contract RCA

Date: 2026-09-28. Status: approved fixture-only changes implemented; 39 distinct
focused tests pass. One rebuilt offline canonical completed its original analyzer
and receipt, but the encompassing fixture failed a later receipt-status assertion.
No production change, live GUI/NKN run, portable build, full matrix, commit, or push.

## Implemented and verified

The implementation follows the approved
`docs/file-transfer-canonical-timeout-contract-review-20260928.md`:

- Only `CanonicalFallback_RequiresOriginalHandoffsBeyondOldOriginCap` receives
  the existing helper's ten-minute timeout override. The shared five-minute
  default, GUI limits, inner 1,800-second analyzer deadline, and native 20,000 ms
  active-work limit are unchanged.
- The exact canonical-fallback fixture writes diagnostic-only checkpoints,
  capped at 16 records and 8 KiB per UTF-8 record, including newline. Each write
  is flushed and closed. Other profiles remain inactive. Existing output is
  preserved; original observations/profile strings are reused, not reevaluated.
- The canonical-fallback mode guard requires authoritative full/compact stream
  evidence and parity, separately from unchanged legacy toy-case expectations.
  As approved, it still requires a proof_incomplete receipt and original
  incomplete-proof rejection. The new canonical evidence below shows that this
  retained receipt-status assumption needs a further contract correction.

Verification artifacts are under
`artifacts/canonical-timeout-implementation-20260928/`:

| Check | Outcome |
| --- | --- |
| Initial timeout regression against old caller | Failed as expected |
| First focused lane | 35 passed, two test-assertion failures |
| Narrow follow-up | Five passed; 39 distinct tests green in total |
| Affected PowerShell support scripts | Nine parse successfully |
| Scoped whitespace check | Passed |
| Nonincremental smoke rebuild | 0 errors, 357 existing warnings; 22.21 seconds |
| Single offline canonical | Failed after 330.276 seconds, not timed out |

The two initial failures were exact exception-type assertions: malformed JSON
raised JsonReaderException, a JsonException subclass. The assertion was corrected
to allow subclasses; actual tree termination and retained-prefix checks had
passed. Only those tests, the small timeout-source check, and two newly added
projection checks were run afterward. No unrelated aggregate/native-read lane
was rerun.

Focused coverage includes both PowerShell hosts; normal/profile isolation;
16-record and exact 8,192/8,193-byte boundaries; existing-file protection;
write failure and external mutation; short process-tree timeouts and truncated
record handling; original-error preservation without repeated predicate reads;
16 proof-mode/error cases per host; and original parser/evaluator/profile-call
counts in the generated worker projection. No production output pump changed.

## Latest canonical evidence

Directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-f7163b0ad67842849573270fe80a49f5`.
Detailed observations are retained in
`artifacts/canonical-timeout-implementation-20260928/CANONICAL-OBSERVATIONS.json`.

- GUI child PID 6220 exited 0 with complete output capture and no timeout or
  deadline extension. Its elapsed time was about 107.66 seconds.
- Primary PID 12708 exited 0 without timeout. Its original stdout confirms
  **three coordinator evaluations and one native source read**. Ordinary stdout,
  stderr, diagnostics, and the original terminal receipt are present.
- All **73,699 source lines and physical origins** completed, with 1,152 origin
  batches. Origins are strictly increasing and positive, from physical line 5
  through 101,829. Source/retained/writer/semantic counts are respectively
  101,843 / 101,841 / 101,843 / 73,699.
- The source/origin checkpoint records **14.6134588 seconds** cumulative native
  active work: 13.3684117 seconds reading and 1.2076507 seconds origin-batch work,
  plus attach/open work. This is a checkpoint measurement, not a claim of the
  final native clock after every later custody operation.
- Binding returned the actual pair. Original cleanup confirms bind_complete=True,
  open_handles=0, evaluation_current=False. The original terminal fixture marker
  also confirms that binding and cleanup assertions completed.
- Six complete checkpoint records total 3,053 bytes. Source/origin, profile,
  binding, and cleanup text match ordinary captured output. These diagnostic
  files are not receipt substitutes or authority inputs.
- The receipt's before/after input SHA matches
  `dafbddec7f7b6423669e1628da8c2c7e3cd73c95f7da19b3c7107a8010e90540`,
  with both line counts 73,699 and analyzerInputUnchanged=true. All 11 artifact
  hashes named by the receipt match their files.
- Full/compact snapshots are SnapshotV1Authoritative, stream-complete, with
  artifact evidence and parity passing: 1,496 boundaries/transitions, 68 effects,
  and three fallback-tail peer-proof bindings on each side.
- The efficiency stage completed in 33.246 seconds. Analyzer stage totals are
  205.773 seconds overall, including 66.504 seconds parse, 10.473 seconds index,
  and 128.553 seconds artifact work. It was not demonstrated to be hung by the
  preceding 300-second interruption.

Both 256 MiB native payloads have the expected SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`;
exclusive read/write access succeeds. Primary/GUI/native PIDs 12708, 6220, 10816,
and 23412 are all gone. This external cleanup check is separate from the observed
in-process primary cleanup assertion.

## First observed failure and RCA

The original receipt says:

| Field | Actual value |
| --- | --- |
| owner | RouteAcceptance |
| exitCode / timedOut | 0 / false |
| status | completed |
| completedProofArtifacts | true |
| coordinatorSnapshotMode | SnapshotV1Authoritative |

`Support/FrontierRelayAnalysis.ps1:69` still assigns proof_incomplete as the
expected receipt status for canonical-fallback. The next guard short-circuits on
that status mismatch and throws `Original analysis owner, source passes or
terminal receipt changed`. The outer attempt wrapper subsequently reports
`Original incomplete-proof rejection changed`. The canonical test fails at its
original nonzero-exit assertion. The new mode helper is **not reached**.

This identifies the first failing operand; it does not establish that every
later parent-side counter or cleanup assertion would pass. In particular, the
parent's hash/count invocation counters were not captured and their assertions
were short-circuited. Do not infer them from the primary's three-evaluations/
one-read output.

The review retained a stale toy-fixture assumption: that incomplete route proof
necessarily means incomplete analyzer artifacts. Fresh evidence disproves it.
`Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts` requires complete
authoritative snapshot evidence, report presence, zero hard-failure counts, and
present verdict fields. It does not require those route/operator verdicts to be
passing. The receipt writer uses completed when the successful analyzer produced
those artifacts with unchanged input.

Consequently, a **completed analysis receipt can truthfully describe an
unsuccessful route-proof result**. This run's operator report remains
INCONCLUSIVE, gate_status=fail, acceptance_failure_class=incomplete_proof, and
runtime_correctness_verdict=incomplete_proof. Its first reported route-proof edge
is `filetransfer_gui_live_reactivation_action_window_missed`, reason
`fallback_reactivation_unlock_not_invoked_before_completed_transfer`.
There is no demonstrated live route-cycle pass or second published file_tuna_v4
route. The receipt is not evidence that runtime route acceptance was relaxed.

## Recommended next bounded correction — not implemented here

Review and approve a canonical-fallback-only fixture contract that separates
analysis completion from route acceptance:

1. Require the original completed receipt, successful/no-timeout analyzer,
   unchanged input, authoritative stream/parity evidence, all existing identity,
   hash/count, original-evaluation/source-pass, and cleanup assertions. Preserve
   fail-closed behavior for missing, malformed, foreign, or mutated evidence.
2. Independently require the retained operator/route result to remain incomplete
   and non-accepted for this fixture. Do not equate completedProofArtifacts with
   successful live proof or alter coordinator/runtime rules.
3. Replace the fixture's expected analyzer-incomplete exception with an explicit
   canonical-fallback stop-before-route-acceptance outcome, analogous to its
   existing canonical-source stop. Keep toy cases and their incomplete-artifact
   expectations unchanged; do not accept either status opportunistically.
4. Update the canonical helper and focused contract cases accordingly. Cover both
   dimensions: complete analysis plus incomplete route proof is this fixture's
   expected result; missing artifacts, successful-route promotion, lost ownership,
   changed counters, and missing cleanup remain failures. If counter diagnostics
   are needed, log already-held values without new reads/evaluations.
5. After approval and focused tests, rebuild and run one offline canonical. Keep
   the newly approved 600-second outer limit; no further increase is indicated.

No additional runtime edit, receipt-status relaxation, canonical retry, or live
test was performed after this new first failure. The approved implementation and
its failing end-to-end evidence are preserved rather than relabelled as a pass.
