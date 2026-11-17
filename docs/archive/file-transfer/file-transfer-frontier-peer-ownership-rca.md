# Frontier peer ownership and joined-fixture stack RCA

Reviewed 2026-09-12 against HEAD
`63df93b28cb9a8ba4924df97c4fa72698fd4fe4a` plus the uncommitted changes below.
The test-frame blocker and exact publication-ownership admission edge are resolved.
The latter adds read-only producer evidence and strict consumer binding, not runtime
authority. The admitted stalled obligation remains unresolved; source supersession
is still unproven in the joined capture.

## Verified test-frame fix

The rejected-confirmation theory reproduced its inbound snapshot-startup stack
overflow in a fresh normal Debug host. A child-process-only, method-specific JIT
diagnostic measured a 1,402,768-byte fixed reservation in
`RunJoinedFallbackYieldAsync.MoveNext`, active during context construction.
Inbound `StartCoordinatorSnapshotStreamLocked` additionally reserved 138,960
bytes; the fixture factory reserved 464 bytes. These are selected frames, not
the complete chain, OS stack size or an identified fault instruction.

Moving only the two whole-state equality assertions into a synchronous helper
was insufficient: the next run still overflowed deeper in snapshot fingerprint
construction. Moving the three existing retry-result assertion branches into
separate local functions reduced the shared async frame to 84,832 bytes. All
assertions, actual queued authority dispatches, confirmation/compensation checks
and replay checks remain at their original sequence points. Both rejected-
confirmation directions pass. No runtime initializer or compensation logic changed.

The separate local retirement control initially crashed without a retained stack;
its original cause must not be inferred from the confirmation crash. Its caller
frame measured 581,952 bytes. Extracting its whole-state equality advanced the
test beyond setup and exposed a retained stack overflow in the mutation-guard
path, ending at `get_CurrentBridgeRecoveryDecision` through responder snapshot
construction. The selected reservations at that stage were:

| Method | Fixed reservation, bytes |
| --- | ---: |
| Local handoff async caller | 443,168 |
| Availability mutation-guard async helper | 190,064 |
| Responder attempt snapshot, public overload | 150,368 |
| Responder attempt snapshot, internal overload | 86,960 |
| Responder snapshot construction | 287,280 |
| State-boundary snapshot construction | 5,504 |

Those frames do not constitute a complete stack measurement. Read-only-reference
arguments removed unnecessary persistent-state copies, allowing mutation checks
to advance, but a later availability callback still overflowed in aggregate
transition logging. The final bounded test fix retains the already reflection-
boxed persistent states at their original capture points and unboxes them inside
synchronous assertion helpers. Equality remains value equality, not object identity.
Held authority/release extraction and final retirement checks use those captured
values; no current state is substituted for a historical assertion snapshot.

The local caller now reserves 35,712 bytes, and the mutation helper 85,984 bytes.
The focused stalled-retirement case passes. The shared non-stalled control also
passes in the affected lane. The original unstamped native crash remains without
a complete fault attribution; later retained managed stacks and the measured
reductions support this bounded fixture-frame remedy, not a general production RCA.

No prewarming, test reordering/quarantine, skipped variants, enlarged stacks,
thread/JIT policy changes, authority-history clearing or runtime changes were used.
Diagnostic runs only enabled selected JIT output in their child process. Ordinary
verification ran without those variables. Build commands disable only the
out-of-workspace Avalonia telemetry target via `-p:UsedAvaloniaProducts=`.

## Original evidence edge: exact publication ownership

The earlier joined capture remains unchanged:
`artifacts/frontier-joined-overlap-20260912/joined-overlap-09.log`, SHA-256
`06238BBE6106091DB2C5FB52208B2106DCFE152408E8413DE88F6959D108AA1C`.
Its actual sender and receiver share writer `p42392r6a5294951f024b01` and the same
transfer. Inbound fallback publication 55, outbound Tuna publication 501 and
inbound hold 559 enter one writer/session/transfer timeline. The admission loop
treats publication 501 as invalidation of the receiver's source and admits no hold.
The observed quartet remains `0/0/0/0`; this is not recovery or approved counting.

Inspection narrows the prerequisite beyond adding direction to an index key:

- The hold has a projected `continuity_coordinator_id` and source fingerprint.
- The old capture's raw route-selected and route-hint accepted records do not carry an exact
  Coordinator instance/publication-receipt binding. Session IDs and the selected
  transition authority are redacted in this capture.
- Validated snapshot boundaries contain separate Coordinator identities and
  publication/transition authority. Their existence does not alone identify the
  owner of an otherwise ambiguous raw publication. Matching a direction, choosing
  the nearest boundary or trusting a diagnostic stamp cannot supply that binding.
- `RoutePublicationToken` currently has session/transfer/direction and owner/route
  generations, but no Coordinator instance field. Extending that authority token
  just for logging would unnecessarily affect runtime token equality and consumers.

Therefore no direction-only filter, writer rewrite or guessed snapshot join was
implemented. The focused admission fixture now explicitly rejects opposite-peer
publications both without owner stamps and with diagnostic-only distinct owner
stamps. Existing current-publication admission, ABA, conflicting-copy, invalid
scope and raw-count checks still pass.

## Bounded publication-evidence contract

1. Add read-only publication evidence that refers to an already Coordinator-authored
   snapshot publication/commit boundary and its exact publication token fingerprint.
   Carry the Coordinator instance, projected session/transfer, direction and exact
   publication binding through the actual logging/observer paths. Do not extend
   runtime authority or reconstruct a receipt from route observations. Review the
   producer and catalog compatibility contract before implementing these fields.
2. Bind the hold's current source to that same validated Coordinator publication.
   A stamp is a lookup key only: a complete, passing, authoritative snapshot and
   exact boundary/operation/receipt match must establish ownership. Missing,
   conflicting or ambiguous bindings retain the current fail-closed behavior.
3. Exclude an opposite peer from source invalidation only after its publication
   is independently bound to a different Coordinator in the same exact transfer
   scope. Preserve both original histories and writer-sequence conflict detection.
   Same-owner ABA, pause/terminal, data-session replacement and unbound poison
   records must continue to invalidate or leave ownership unproven as appropriate.
4. Rebuild and capture only the joined fake-peer test after producer changes.
   Assert the exact raw publication-to-snapshot bindings before running bounded
   direct/indexed/full/compact/shared GUI frontier checks under PS5 and PS7.
   Include missing/wrong/stale binding, duplicate/conflict and same-source ABA
   controls. Stop at the first further missing proof edge.
5. Keep the admitted stalled obligation unresolved under existing policy. This
   admission correction must not promote A to recovered, reinterpret B's stale
   retirement, activate the proposed neutral superseded disposition, or waive
   route/completion gates. A policy/accounting change remains a separate decision.

### Implemented producer and consumer binding

`FileTransferRoutePublicationEvidence` is an internal diagnostic record, separate
from `RoutePublicationToken` and publication authority. Its version-1 raw suffix
contains projected Coordinator/session/transfer IDs, the exact authored boundary
and state generation, the publication operation key and token fingerprint, and
the emitter's exact observed state generation. The existing accepted-receipt index
supplies the reference without issuing authority, changing state or starting a stream.
Absent/unissued/foreign references and completed streams return no evidence.

The reference travels through actual selected/recovered route logs, the observer's
accepted-publication log, and the inbound hold log. The typed snapshot schema,
catalog version/hash and authority token equality are unchanged. Notification
evidence is consumed only for logging, not transport admission or runtime decisions.

The consumer uses the caller's complete, passing authoritative snapshot. It requires
one exact boundary and publication operation, validated accepted receipt, matching
fingerprints/generations, strict route/protocol/owner scope and the exact observed
after-state. Unredacted raw IDs and the hold's continuity Coordinator must agree;
redacted IDs cannot replace the projected snapshot scope. A historical receipt
cannot bind a changed publication or data session. Only an independently validated
opposite Coordinator in the same projected session/transfer can be excluded from
source invalidation. Conflicts, same-owner ABA, pause/terminal/data-session poison,
missing stamps and wrong/stale bindings remain fail-closed. Legacy same-owner
admission is preserved; diagnostic owner stamps alone still cannot split peers.

The saved-capture verifier initially omitted the GUI harness's required
`gui_evidence_sequence` projection. Correcting that verifier input, and forwarding
the already evaluated snapshot at the GUI/retained admission call sites, establishes
consumer parity without changing the GUI's ordering contract. No saved capture was
rewritten. No new report, package, GUI, preflight or live NKN attempt was run.

## Verification and handoff

Artifacts: `artifacts/frontier-blockers-20260912/`.

- `confirmation-fixed-02.trx`: PASS 2/2, normal Debug.
- `confirmation-frame-after.trx`: PASS 2/2 with method-specific diagnostic output.
- `local-retirement-fixed-03.trx`: PASS 1/1, normal Debug.
- `local-frame-after.trx`: PASS 1/1 with method-specific diagnostic output.
- `admission-negative-guards.trx`: PASS 1/1, actual GUI/retained admission checks.
- `affected-contract-lane.trx`: PASS 10/10, one normal Debug affected lane covering
  cross-peer fallback overlap and the two local handoff variants. It includes the
  new A/B repair/replay joined case. These are not additive distinct-test counts.

Earlier aborts and intermediate failures are retained, not relabelled as passes.
The prior capture's PS5/PS7 snapshot/route parity remains separate from its historical
frontier-admission failure. That capture is unchanged; it lacks the new references.

### Publication-evidence verification

Current artifacts: `artifacts/publication-evidence-20260912/`.

- Producer lookup: PASS 2/2; consumer binding/poison matrix: PASS 2/2 (PS5/PS7).
- Existing admission/current-source/unresolved-diagnostic regressions: PASS 3/3.
- Rebuilt joined fake-peer capture: `joined-evidence-01.trx`, PASS 1/1. Log SHA-256:
  `0D47ADE187B1A72538DB12C22BBE9F15A3BEE08508841AAE1BEB8D6B8F7DD16D`.
- All nine emitted references bind exactly under both hosts, including receiver
  fallback publication 55, sender Tuna publication 503 and receiver hold 561.
- Strict snapshots/routes, full/independently regenerated compact evidence and
  shared GUI setup classification pass with cross-host parity.
- Direct/indexed/full/compact/shared GUI frontier consumers agree on `1/1/0/1`,
  `DispatchedStalled`, `SourceDisposition=Unproven`, `ObligationDisposition=Unresolved`.
  A remains stalled; B's stale retirement is not recovery.

The publication contract lane initially passed 24/25. Its sole failure was the
existing `RouteHintObserver_FallbackQuiescentGrantSupersedesOlderDeniedFallbackOwner`
assertion expecting `post_tuna_fallback_tail_quiescent`. The unchanged HEAD decision
path selects outbound fallback publications and requires an exact committed retry
receipt; this inbound fixture has neither. Source inspection establishes the stale
assertion, not a newly introduced runtime regression; a separate HEAD build was not
run. The revised two-direction test checks both publication generations and stored
admission evidence before/after the newer hint. Inbound remains `false/none`;
outbound remains held for `fallback_paired_tail_yield_install_receipt_missing`.
Both cases pass. Runtime behavior was not changed. The other 24 lane tests were not
rerun after this isolated test correction; earlier failures remain in the ledger.

### Next bounded evidence review

Completed by the [repair-owner implementation](file-transfer-checkpoint-role-and-repair-owner-rca.md#implemented-repair-owner-evidence-contract).
The new rebuilt capture has exact repair receipts and proves `SourceSuperseded`
without recovery or count-policy changes. The paragraph below describes the prior
publication-only capture, which remains frozen and is not rewritten.

Admission now reaches the downstream source-supersession diagnostic. The completed
[checkpoint-role and repair-owner review](file-transfer-checkpoint-role-and-repair-owner-rca.md)
shows that hold 561's retained-leg generation 0 and commit 812's durable generation
1 describe different checkpoint roles. The aggregate snapshot's generation 0 is
a third role, the active lease, and cannot substitute for retained-leg evidence.
The candidate direct mapping was withdrawn; the consumer remains fail-closed.
Repair grants A/B also lack exact Coordinator continuity bindings, and their generic
RepairReplay snapshots contain no typed operations. The next implementation must
establish versioned, read-only repair-owner/retained-leg evidence before replacing
the cross-role comparison. No checkpoint acceptance or repair receipt may be invented.
No neutral superseded accounting, target progress or completed transfer is approved
or claimed by this slice.

Changes remain uncommitted. All 127 attempt-freeze and 119 report-6 files retain
their saved hashes; live attempt count stays 12 and retained-report count stays 6.
