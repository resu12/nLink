# Payload-offer diagnostics and bounded RCA — 2026-09-27

Status: failure-only diagnostics implemented; eight distinct focused controls
pass. The one fresh offline canonical attempt remains a failure. It proves the
payload listener expires independently at about 100 seconds, but stops at an
earlier writer-role join guard before offering payloads. The complete cause of
the prior payload-offer failure is therefore not established.

Branch `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-fallback-state-epoch-fix-20260927.md`.

## Bounded changes

Only three existing test/support files changed:

- `FrontierAttemptPayloadWrapper.cs`: failure-only client/server stage,
  exception-type, elapsed-time and UTC diagnostics; server failure also records
  the existing lifetime cancellation state and original cumulative work clock.
- `FrontierAttemptPayloadWrapperProbe.ps1`: records the already-evaluated original
  offer and, only when the original short-circuit would evaluate it, repeat
  result. It preserves the existing assertion and makes no extra authority call.
- `FrontierPayloadWrapperTests.cs`: checks positive silence and expected bad-ACK
  and exhausted-work diagnostic branches using the original integration cases.

The adopter remains compiled only in `NLINK_TEST_ATTEMPT_DISPATCH`. No production
runtime, authority predicate, cleanup decision, deadline, byte/work limit,
acceptance rule, transport policy or origin capacity was changed. Diagnostics
do not call `IsCurrent`, `ScopeCurrent`, or another authority evaluation again.
Client guards rejected before entering the exchange remain silent.

## Focused verification

Results: `artifacts/payload-offer-rca-20260927/results/`.

- `diagnostic-controls.trx`: six passes and two diagnostic assertion failures.
  Normal custody, bad-ACK rejection and default-loader exclusion pass in both
  hosts. Both exhausted-work runs correctly reject at `work_admission`, but the
  test looked only in outer stderr. The original parent forwards the child
  stderr through its captured output, so that selected string was empty.
- `diagnostic-controls-corrected.trx`: both exhausted-work cases pass after the
  assertion inspects combined captured output. Only these affected cases reran.
- `canonical-diagnostic.trx`: one failure, preserved below; no second canonical
  attempt was made.

Final distinct focused passes: **8**, zero skips. The earlier two assertion
failures are retained, not counted as runtime defects or relabeled as passes.
PowerShell parsing and whitespace checks pass. No aggregate lane was needed for
these explicit-profile diagnostic changes.

## Fresh canonical evidence

Directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-4e6fe57df2b043b8848fb3d9e3a701ff`.

Both original app processes completed **268,435,456 bytes** on
`post_tuna_fallback_v6`, with the same session/transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent source/destination hashes match. GUI output contains
`attempt-destination-gui-adoption-ok` and `attempt-sender-source-delivered`.
Both files permit exclusive access after cleanup; GUI 9072, sender 58044 and
receiver 22188 have exited.

The original ledger is **101,877 lines / 92,094,898 bytes**, SHA-256
`bbfbab8b257e607e81ba632e8bd98f309333b1f206c9f861395eebafd5928772`.
At physical lines 997 and 1001, fallback state 2 respectively applies from
previous epoch -1 with credit 512 and rejects its identical repeat as duplicate.
No further epoch fix is indicated by this run.

Retained/writer/semantic outputs have 101,875 / 101,877 / 73,728 lines, totaling
277,480 references. Their existence and cardinalities do not prove downstream
native serialization, custody handoff or primary acceptance.

Derived observations, exact selected source lines and file hashes are in
`artifacts/payload-offer-rca-20260927/canonical-observation.json`.

## Finding 1: independent pre-offer listener expiry is confirmed

The wrapper's original diagnostic is:

```text
payload-wrapper-adoption-failed; stage=connect; exception_type=OperationCanceledException; elapsed_ms=99993; lifetime_cancelled=False; work_ms=0; work_running=False; utc=2026-09-27T11:48:46.2842112Z
```

`AttemptPayloadWrapperForTest.Adopt` starts its connection wait when the GUI
child starts, before transfer, writer closure and ledger processing. It links
the wait to the existing lifetime but also calls `connect.CancelAfter(100000)`.
The observed cancellation is at that independent timer, with the owner lifetime
not canceled and no parent adoption work consumed. It is not a packet, nonce,
native-file or five-second work-budget rejection.

The GUI began at 11:47:05.9817108Z and exited at 11:48:53.1789668Z, approximately
107.2 seconds later. Its unchanged scenario deadline was 210 seconds, plus
120 seconds finalization grace; `timedOut=false`, exit code 1. The full-size app
fixture separately permits 180 seconds. Thus a still-owned, in-budget attempt
can lose its pre-offer listener before it is ready to offer.

The previous attempt reached the payload-offer assertion after approximately
112.5 seconds, consistent with this mismatch. That older artifact has no
client-stage diagnostic. The new attempt did not reach its offer, so it does
**not** prove that the older client failed specifically at connect or rule out
additional older-run failures. The prior combined result/repeat ambiguity is
not retroactively resolved.

## Finding 2: the first missing proof edge in the latest attempt is earlier

The GUI fails at `FrontierAttemptWritersJoin.ps1:112`:

```text
Copy/repeat retired or reissued role join
```

This comes after original writer/cursor/recovery processing and role binding,
but before `FrontierAttemptSourceJoin.ps1` reaches payload offering. No
`payload-offer-failed`, `payload-offer-contract-failed` or native writer-handoff
success marker exists. Original primary analysis did not start. The later
normal-exit and parent-handoff errors are consequences, not separate roots.

Static inspection narrows, but does not fully diagnose, this guard:

- A copied join's `Dispose` cannot retire the original: retirement checks exact
  object reference. The clone-current check short-circuits on that same identity.
- The repeat bind refuses a non-null `roleWriters`; its only assignment is the
  original candidate, with no clearing assignment. Ordinary execution therefore
  points to the original join becoming non-current, not successful rebinding.
- Join validity depends on the original attempt, binding/match, exact manifests
  and validation snapshot. Their ownership checks can retire stale state. The
  file-adoption checks also charge the existing cumulative 10-second GUI clock.
  No branch/clock observation was retained at this failure, so budget exhaustion,
  owner retirement and other native-scope failures cannot yet be distinguished.
- Wrapper listener disposal cancels its own lifetime and closes its own pins;
  it does not close the GUI request or mutate this role join. There is no basis
  to attribute the join rejection to the listener timeout.

The same guard had failed in an older Windows PowerShell positive and passed an
unchanged rerun, as recorded in `file-transfer-payload-wrapper-custody-20260926.md`.
That history is evidence of recurrence, not proof of a cause or permission to
retry until green.

## Next bounded sequence and conditional fix plan

1. **Diagnose the earlier original role-join invalidation first.** Capture the
   already-evaluated original-current/repeat results while preserving exact
   short-circuit order. At the first existing rejection, retain its branch and
   original owner/cumulative-clock observations; do not call `IsCurrent` again
   to diagnose it. Check clone disposal and repeat refusal with directly affected
   controls. Make no work-budget increase, cached authority result or heuristic
   retry. If another run is needed, use one separately bounded offline attempt
   after these diagnostics are verified, not a live/full matrix run.
2. **Fix only the proven earliest cause.** A stale owner must remain rejected.
   If the legitimate path exhausts its cumulative clock, review the actual
   expensive path and contract before changing work or validation frequency.
   Do not infer a clock defect merely from this hypothesis.
3. **Then reconcile the confirmed listener-lifetime mismatch.** Recommended
   contract: pre-offer waiting remains bounded by the original request/GUI
   lifecycle and existing outer deadline, rather than an unrelated 100-second
   timer. Existing request-close/GUI-exit callbacks cancel the listener. Prove
   all disposal/no-offer paths before removing the independent timer; do not
   replace it with a larger arbitrary number or introduce a new authority token.
   Preserve the 10-second connected exchange, original 5-second active-work
   clock, 8192-byte offer, 4 MiB native facts cap and exact native ownership.
4. **Verify the eventual listener change in isolation.** Require a legitimate
   delayed offer beyond the former wait to succeed while its original owner is
   live, and original-owner cancellation/GUI exit/no-offer cleanup to terminate
   the wait. Preserve wrong-ACK, duplicate/copy, nonzero-exit and exhausted-work
   rejection. Use focused controls, then at most one canonical attempt; any new
   earlier edge remains a failure and must be reported.

Steps 2–4 are a plan, not implementation in this checkpoint. In particular the
100-second timer has not been removed or increased. Native writer handoff and
original primary evaluation remain required. The route-cycle acceptance still
requires a second coordinator-published `file_tuna_v4`; transfer/SHA success does
not replace it. No live test is due at this boundary.

## Preservation

All 2,197 preceding checkpoint pins matched before edits. The 391-entry dirty
baseline was retained; only the three diagnostic/test files above were edited
within it. Prior artifacts and reports are immutable. The recovery checkpoint
is `artifacts/payload-offer-rca-20260927/FINAL-VERIFICATION.json`.

No functional fix, portable rebuild, live network run, full GUI matrix, commit
or push occurred.
