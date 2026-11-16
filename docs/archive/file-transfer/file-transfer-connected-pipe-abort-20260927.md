# Connected pipe cancellation and listener lifetime

Date: 2026-09-27. Status: both fixes verified; one canonical attempt advanced to the parent analysis boundary and failed there.

## Implemented scope

User approved the bounded connected-pipe cancellation correction and resuming
the previously approved listener-lifetime change.

In the explicit `NLINK_TEST_ATTEMPT_DISPATCH` payload adopter only:

- The connected exchange registers its existing linked cancellation token to
  dispose that exact pipe instance. The registration is scoped with `using`,
  so every return/exception unregisters it before leaving the exchange.
- The pre-connection wait now uses the original `lifetime.Token` directly.
  Only the independent 100,000 ms pre-connection timer was removed.

The cancellation callback closes I/O only. It performs no scope evaluation,
does not take the owner lock, does not call adopter disposal, and does not change
custody, route or acceptance state. Existing adoption failure cleanup remains
responsible for retirement and native-handle release. Capturing `pipe.Dispose`
as a method group binds the exact pipe, not a later field lookup.

The registration is disposed before the outer catch/finally cleanup acquires
adopter locks. Concurrent original-lifetime cancellation and explicit disposal
remain idempotent pipe closure; successful exchange/receipt still uses every
original identity, scope and commit gate. Timing remains cooperative, not a new
hard real-time scheduling guarantee.

Unchanged: whole-client and connected-server 10,000 ms deadlines, cumulative
parent 5,000 ms work and GUI 10,000 ms work, original owner/report identity,
OS peer PID, exact attempt, 8192-byte packet bound, native facts and two 256 MiB
file checks, nonce/commit/receipt, normal-exit/captured-output take, native-writer
custody exception, outer watchdogs and all authority flags. No retry/reopen,
timeout increase, coordinator, transport or acceptance change was introduced.

## Test-only controls

Added connected request-dispose, adopter-dispose and deadline/dispose-race
controls on both PowerShell hosts. They use the original owner/reports/adopter,
an actual connected local pipe, and verify unsuccessful task completion, closed
endpoint/handles, no custody/revival and unchanged parent work.

The new `stall-commit` negative peer performs the real original authentication
and ACK, then deliberately withholds its commit and waits without client-side
cancellation for server closure. This fault is confined to the explicit test
profile and selected fixture case. It is not a change to the normal client's
deadline or protocol. It proves server termination instead of letting client
timeout mask a pending server read. Existing original-owner, repeat, cleanup
and exclusive-file-release assertions still run.

Corrected the prepared delayed-offer test's post-cleanup artifact lookup to use
the full carrier fixture's actual `native-carrier-received` directory. No data,
expected byte count or verification requirement was relaxed.

## Completed verification

Artifacts under `artifacts/connected-pipe-abort-20260927/results`:

| Gate | Result |
| --- | --- |
| Connected packet/commit stalls and connected-disposal controls | 10/10 passed |
| Delayed offer before listener change | 2 expected failures at original `connect` cutoff |
| Same delayed offer after listener change | 2/2 passed |
| Stabilized wrapper lane and native-writer positives | 50/50 passed |
| Rebuilt offline canonical attempt | 1 failure after successful native-writer and parent handoffs |

Before the listener change, Windows PowerShell reports listener expiry at
100,016 ms and an original offer at 105,010 ms; PowerShell 7 reports 100,012 ms
and 105,008 ms respectively. Both fail at connect with no original lifetime
cancellation and zero parent work. These are retained expected baseline
failures, not an accepted transfer.

After the listener change, original authenticated offers succeed at 105,001 ms
(Windows PowerShell) and 105,013 ms (PowerShell 7). The same controls require
normal-exit terminal take, native verification and exclusive access to both
256 MiB files after cleanup. Neither original work clock is stopped/reset to
stage the wait; the existing 210-second outer scenario allowance is used.

The previously failing Windows PowerShell silent-packet control now completes
at 10,011 ms, with lifetime not cancelled and parent work 0/stopped. Its
`EndOfStreamException` is diagnostic; success of the negative control also
requires retirement, no custody and closed resources. Both authenticated
commit-stall controls reach `commit_read`, terminate under the unchanged
deadline, reject the offer, and release the payload files.

The two delayed controls were not repeated in the stabilized lane; the combined
current-source gate has 52 distinct passing controls, including all directly
affected wrapper cases and native-writer positive handoffs. The stabilized run
completed in 20 minutes 47 seconds with zero failures/skips. No unrelated
recovery or aggregate lane was run. The smoke assembly was then rebuilt with
zero warnings/errors before the one offline canonical attempt.

## Recovery and boundaries

### Single canonical attempt: progress and next first boundary

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-6a09156f90e146d38439b9785cb5a201`.

The original GUI offer, wrapper terminal take/verification, GUI native-writer
handoff, wrapper-to-parent offer and parent custody checks all complete. The
GUI exits 0 with output capture complete and no watchdog/terminal extension.
Its cumulative active work reaches 6,336.9091 ms of the unchanged 10,000 ms
allowance. Both apps report 268,435,456 bytes on `post_tuna_fallback_v6`, matching
session/transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Both payload files allow exclusive read/write opening after cleanup. These are
progress and cleanup results, not route-cycle acceptance.

The test then fails at `FrontierPayloadPrimaryTests.cs:51` because the parent
fixture exits 1. Its retained error is `Original incomplete-proof rejection
changed` from `FrontierAttemptDispatch.ps1:92`. Neither the copied primary worker
script nor primary stdout/receipt is present. Primary analysis is not established.

Source/artifact review identifies a definite fixture mismatch at
`FrontierAttemptPayloadParentProbe.ps1:75`: `AnalysisBoundary` always enumerates
`native-received`. This full case has only `native-carrier-received`. Repeating
that enumeration read-only after the run raises `ItemNotFoundException` with
`PathNotFound,Microsoft.PowerShell.Commands.GetChildItemCommand`. No process,
transfer, authority evaluation or analysis was replayed for this check.

Important limitation: the outer fixture stores its original exception in
`$failure`, then replaces it with the generic assertion without printing it.
The boundary also checks parent currentness before directory enumeration. The
retained artifact therefore does not prove which earlier predicate first threw.
The missing directory is a confirmed defect and leading explanation, **not** a
claim that the masked original exception has been recovered. Do not infer a
runtime ownership failure or tune any clock from this artifact.

Recommended next bounded step:

1. Preserve the already-caught original failure in fixture diagnostics before
   the unchanged expected-incomplete-proof guard; add no authority evaluation.
2. Make the protection probe select the directory declared by the existing
   fixture case (ordinary versus full carrier), not an existence-based fallback.
   Keep currentness and exclusive-open rejection intact, and reject missing or
   ambiguous received payloads rather than vacuously checking only the source.
3. Verify both fixture layouts and missing/ambiguous cases directly, then rebuild
   and run only one targeted canonical attempt. Keep the original analysis and
   route-cycle acceptance rules unchanged; retain any next first refusal.

No parent-boundary code was changed and no canonical retry was made here.

Preflight verified all 4,061 previous pins, with no mismatches, at HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`, 407 dirty entries.
Previous checkpoint SHA-256:
`2621f061711efe7e82a70068f597e277bb541758550c78cc6796986f1d842c47`.

No portable rebuild, live GUI/NKN run, full matrix, commit or push was performed.
The canonical attempt was an offline native-process fixture; successful
payload/SHA/handoff verification does
not replace the required second coordinator-published `file_tuna_v4` route.

This step stops at the new boundary without retries or acceptance relaxation.
Recovery seal: `artifacts/connected-pipe-abort-20260927/FINAL-VERIFICATION.json`.
