# Offline canonical analysis/route-proof contract correction

Date: 2026-09-28. Scope: approved fixture-only correction and one offline canonical
rerun. Production runtime and live route acceptance remain unchanged.

## Correction

The previous canonical completed analysis successfully but failed the fixture's
stale expectation of an incomplete-artifact receipt. The receipt's `completed`
status describes analysis artifact completeness, not successful route proof.
See `docs/file-transfer-canonical-timeout-implementation-20260928.md` for that RCA.

The canonical-fallback fixture now requires:

- Original `RouteAcceptance` ownership; completed receipt; successful available
  exit code; no timeout; unchanged analyzer input; authoritative full/compact
  stream evidence and passing parity.
- Unchanged parent hash/count/launch assertions (2/2/1), native source origins,
  original three evaluator calls and one native read, payload binding markers,
  closed custody/payload handles, original identity restoration, one analysis
  call, and no route publication by the outer attempt fixture.
- Independently, operator `INCONCLUSIVE`, failed gate, incomplete-proof acceptance
  and runtime verdicts, zero hard failures, and failed live-route-epoch proof.
- An exact `Offline canonical-fallback verification stopped before route-cycle
  acceptance` sentinel, after every preceding assertion. The dispatch wrapper
  accepts this sentinel only for the exact canonical-fallback profile.

Canonical-source retains its own exact sentinel. Legacy toy profiles retain
their incomplete-artifact receipt/exception contract. Missing, malformed,
foreign, changed, timed-out, incomplete-analysis, or promoted-route evidence is
not an alternative success mode. No additional authority evaluation is added.
The new diagnostic line prints already-held owner/status/counter values; it does
not repeat the source reads or evaluator calls.

## Focused verification

- Revised positive control failed against the original helper, reproducing the
  stale receipt assumption before the correction.
- Three focused xUnit tests pass: both PowerShell hosts each execute 32 proof
  controls and 30 actual fixture-tail guard controls; the third test checks that
  the ten-minute override remains canonical-only.
- Guard controls execute the fixture's own code, covering ordinary, canonical
  source, and legacy profiles; ownership, unchanged input, hash/count/launch
  checks, native source/origins, custody/payload cleanup, evaluator marker,
  exact sentinel separation, nonpublication, and identity restoration.
- Five affected PowerShell support files parse. Scoped whitespace check passes.
- Nonincremental smoke build: zero errors, 357 existing warnings, 32.24 seconds.
  No source was edited after that build before the canonical launch.
- Preflight: all 732 previous pins matched before editing; all 451 starting dirty
  entries matched their previous hashes/status. Before canonical launch, only six
  existing fixture files and the rebuilt smoke assembly had changed, with one
  new fixture control script. No production pin changed or prior dirty file was
  removed. Details: `artifacts/canonical-receipt-contract-20260928/PREFLIGHT.json`.

## Single offline canonical

Directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-04bc0771d73648bdba12c8211a483448`.

**Passed in 330.6642405 seconds.** Exactly one canonical run was made. The outer
600-second budget, native 20-second active-work budget, and original inner
deadlines are unchanged. This local native/fake-carrier fixture is not a live
GUI/NKN route-acceptance run.

The previously short-circuited parent guard now passes, with captured original
values: owner=RouteAcceptance, status=completed, hashes=2, counts=2, launches=1.
The exact canonical-fallback stop marker is followed by the original parent
finish marker and dispatch success. Original identity restoration and absence
of outer route publication were therefore checked, not inferred from payload SHA.

The receipt reports exit 0, no timeout, completedProofArtifacts=true, unchanged
input, and identical before/after SHA
`488056bd6d81054fd82c92d75fa9d033d857207f3e7d8f6c8b1197439dd97680`.
All 11 receipt artifact hashes match. The original stdout confirms three
coordinator evaluations, one native read, 73,713 source lines/origins, and 1,152
origin batches. Origins are positive and strictly increasing, from physical
line 5 through 101,847. Source/retained/writer/semantic counts are respectively
101,859 / 101,857 / 101,859 / 73,713.

The source/profile checkpoint records 13.9568441 seconds cumulative native
active work (not a final-clock claim after later custody operations). Six complete
diagnostic records total 3,050 bytes; source/origin, profile, binding and cleanup
strings match ordinary output. Binding succeeded; cleanup confirms
bind_complete=True, open_handles=0, evaluation_current=False. Both authoritative
snapshot views are stream-complete, artifact evidence/parity pass, with 1,496
boundaries/transitions, 68 effects and zero legacy selections. Analyzer total
time is 209.032 seconds, including 65.932 seconds parse, 10.489 seconds index and
132.379 seconds artifact work.

Both 256 MiB payloads have SHA
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`
and can be opened exclusively read/write. Native roles report matching
session/transfer identity and post_tuna_fallback_v6. GUI child 55548 exited 0,
without timeout or deadline extension, with complete output capture. Primary
51268 and native actors 39404/20464, as well as the GUI child, are gone.
Detailed evidence is in
`artifacts/canonical-receipt-contract-20260928/CANONICAL-OBSERVATIONS.json`.

## Remaining route-proof edge — not accepted or changed

The operator result remains INCONCLUSIVE, gate_status=fail,
acceptance_failure_class=incomplete_proof, runtime_correctness_verdict=incomplete_proof,
hard_failure_count=0. Route consistency passes but live_route_epoch_proof_verdict
is fail. The original reports identify the same first edge:

- Class: `filetransfer_gui_live_reactivation_action_window_missed`.
- Reason: `fallback_reactivation_unlock_not_invoked_before_completed_transfer`.
- Variant: `fault_admission_tracker_backlog_delayed_prearmed_unlock_past_terminal`.
- Reported retained-source line 73,649 / writer sequence 59,304 is a completed
  outbound epoch 2 on post_tuna_fallback_v6.

This is a passing offline custody/analysis integration test that truthfully
retains a failed route-proof result. It is not a second published Tuna route or
a live route-cycle pass. Primary diagnostic_receipts remains zero; no eligible
primary-owned witness was invented from the later report inspection.

The next bounded step is to review the missed reactivation-action window and
prepare its smallest evidence-backed plan using this fresh artifact. Do not
change runtime authority, tune throughput, or run a full matrix based on this
fixture success. Any subsequent runtime validation needs a fresh portable build
and the required escalated targeted live preflights. No second canonical retry
or runtime correction was performed here.

No live network test, portable build, full matrix, commit, or push is in scope.

## Preservation

`artifacts/canonical-receipt-contract-20260928/FINAL-VERIFICATION.json` chains the
previous seal, records the scoped changed pins, and retains this run's source,
receipt, reports, checkpoint and payload hashes. The branch remains v0.7.0 at
`a653d4063070235c71fe211480aeb37863d9b2a4`. The worktree has 453 dirty entries:
all 451 prior entries retained, six intentionally changed fixture files, and
two additions (the guard-control script and this report). No production file or
unrelated prior entry changed during this correction. The final independent
rehash is recorded in the adjacent `FINAL-CHECK.json`.
