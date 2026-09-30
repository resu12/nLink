# Payload listener lifetime — RCA and bounded fix plan

Date: 2026-09-27. Status: **review complete; implementation not approved or
performed in this step**. Continues the recovery-buffer-v1 checkpoint.

## Outcome

The current first missing edge is the GUI-to-wrapper payload connection. The
wrapper's independent 100-second pre-connection timer expires before the valid
GUI attempt reaches its offer. The latest run directly records the client's
failure at `connect`; it no longer stops at the earlier work/role-join guards.

Recommend `payload-listener-lifetime-v1`: await connection using the existing
original wrapper lifetime, bounded by the existing request/GUI lifecycle and
outer watchdog. Remove only the independent pre-connection timer. Preserve
every connection, identity, custody, commit, work-budget and acceptance check.

This concerns the adopter compiled only under `NLINK_TEST_ATTEMPT_DISPATCH`.
It is not an NKN transport timeout or a shipped GUI connection-policy change.

## Latest evidence, not a replay

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-09b44c6ae3c64d028d35bc0078d73244`.

| Observation | Evidence |
| --- | --- |
| Original GUI starts | `filetransfer-gui-evidence-finalization-state.json`: 14:39:16.0756521 UTC |
| Wrapper connection wait expires | `result.txt:1`: 14:40:56.4088860 UTC, 100,006 ms, `OperationCanceledException`, lifetime not cancelled, work 0 ms/stopped |
| GUI offer cannot connect | `gui-smoke-stderr.log:1`: 14:41:08.6037021 UTC, 10,016 ms, `OperationCanceledException` |
| Offer contract remains rejected | `gui-smoke-stderr.log:2`: original result false; expected true; repeat not evaluated |
| Original GUI exits | Finalization state: 14:41:08.7383265 UTC, exit 1, `timedOut=false`; scenario deadline never requested |

Subtracting the rounded client elapsed diagnostic from its failure timestamp
places the offer operation's start at approximately 14:40:58.588 UTC, about
2.18 seconds after the listener's failure diagnostic. This is a derived
operation-start estimate, not an explicit connect-start event.

The recorded scenario allowance is 210 seconds, with 120 seconds finalization
grace: initial hard deadline 330 seconds, maximum configured envelope 540
seconds. No terminal-phase extension was adopted. The GUI exited after about
112.7 seconds. Its cumulative active-work clock reached only 4,568.2407 ms of
the unchanged 10,000 ms allowance. Recovery capture took 116.0135 ms with 182
full checks and zero check failures.

Thus the listener's cancellation is independent of both the scenario watchdog
and the cumulative work limit. Both apps' fallback/SHA completion is preserved
but cannot replace native handoff, primary analysis or route-cycle acceptance.

## Source-level cause

1. `FrontierAttemptDispatchWrapper.ps1:43–47` starts the adopter immediately
   after the original GUI launch reports are available, before transfer,
   writer closure and evidence processing finish.
2. `FrontierAttemptPayloadWrapper.cs:188–192` links a token to the original
   lifetime and also schedules `CancelAfter(100000)` for `WaitForConnectionAsync`.
   The 100-second value is independent of the full-size fixture's existing
   210-second scenario selection (`FrontierAttemptDispatch.ps1:73`).
3. On failure, `Adopt` reports the stage, retires the adopter and disposes its
   pipe. The one-shot registration cannot restart. The later original offer
   uses its unchanged 10-second deadline (`FrontierAttemptPayloadWrapper.cs:58`)
   and fails to connect; no packet, native-file validation or commit is reached.

The earlier payload-offer RCA retained uncertainty because its then-current
run stopped before offering. This latest source-matching artifact closes that
diagnostic gap. It does not retroactively establish causes for older runs.

## Proposed contract: payload-listener-lifetime-v1

Replace only the pre-connection linked-token/timer block with:

```csharp
await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
```

This removes the redundant child token source as well as its independent timer;
it does not replace 100 seconds with another number or reopen/retry the pipe.

The material change is resource lifetime: a still-owned listener may remain
pending beyond 100 seconds, up to existing lifecycle cancellation/cleanup.
No new authority, successful-currentness cache, readiness receipt, user-supplied
timeout or budget is introduced. Waiting must consume no original parent work.

| Boundary | Required behavior after the change |
| --- | --- |
| No client yet | Original lifetime controls cancellation; no payload facts, take or authority become available |
| Request closes/disposes before offer | Existing close callback retires the listener and completes adoption unsuccessfully |
| GUI exits before offer, including exit 0 | Existing exit callback retires it because no commit exists |
| Wrapper cleanup/watchdog unwind | Existing explicit payload disposal closes the pipe before request disposal |
| Client connects | Existing scope and exact OS client PID checks still run; no wait outcome substitutes for them |
| Connected exchange | Existing linked 10,000 ms server deadline and entire client 10,000 ms deadline remain |
| Validation/verification | The same original cumulative 5,000 ms parent-work clock and native checks remain |
| Commit, receipt and terminal take | Original nonce, normal-exit, captured-output and exact-object gates remain |

`Start` already registers original request-close and GUI-exit waits
(`FrontierAttemptPayloadWrapper.cs:150–160`). `RequestClosed` retains a narrow
post-adoption native-writer handoff exception (`FrontierAttemptPayloadWriters.cs:21–29`);
do not alter it. Before an offer, there is neither writer custody nor a writer
take, so that exception cannot authorize keeping the listener alive.

`Dispose` already cancels the lifetime, closes the pipe, unregisters waits and
releases duplicated handles (`FrontierAttemptPayloadWrapper.cs:367–379`). The
fixture injects explicit payload disposal into the common wrapper's `finally`
(`FrontierAttemptDispatchWrapper.ps1:62–64`; underlying finally at
`tools/Run-FileTransferTunaGuiSmoke.ps1:879–905`). Its scenario-deadline seam throws
and unwinds through that cleanup; it must remain a failure, never be bypassed.
The common wrapper's scenario/finalization watchdog and any already permitted
terminal-phase handling remain unchanged.

These cleanup paths are established by inspection here, not newly verified
pre-offer guarantees. Existing post-adoption GUI-death tests are not substitutes
for the pre-offer tests below. Cancellation remains cooperative, with no new
hard millisecond scheduling guarantee. Wrapper process death releases its
OS-owned resources; it never manufactures a successful adoption.

## Implementation and verification gates if approved

1. **Reproduce and verify lifetime boundaries first.** Add directly scoped
   controls on Windows PowerShell and PowerShell 7 using the actual original
   adopter and genuine owner/report objects. A delayed-offer control must cross
   the real former 100,000 ms wait while the original owner is alive, then
   exercise the actual authenticated offer/commit path. Before the fix it must
   fail at the listener wait; after it, successful adoption must still require
   the original exchange. Use an existing sufficient outer scenario allowance;
   never reset/stop an original work clock or lengthen a deadline to stage it.
   Shortened test waits are useful for cleanup but are not proof of crossing
   the former 100-second boundary. Record actual elapsed wait and clock identity.
2. **Prove cleanup and unchanged rejection.** Cover no-offer original request
   close and genuine request disposal, normal/nonzero/killed GUI exit, explicit
   adopter disposal, wrapper-finally/watchdog unwind, startup failure, and
   cancellation racing connection. Require task completion, a closed endpoint,
   no provisional/taken custody, no revival, and released handles. Exercise a
   connected peer that never finishes its packet/commit; it must still lose the
   original 10-second deadline. Retain wrong PID/identity/ACK, copied/repeated
   inputs, exhausted parent work, and native-writer retirement rejection. Do not
   claim rejection solely from a particular exception string. If an existing
   cleanup path fails, retain that first failure and review the required local
   correction before widening this one-wait change.
3. **Stabilize, then run one canonical attempt.** Run the directly affected
   `FrontierPayloadWrapperTests` lane once, the native-writer positive handoff
   controls and default-loader exclusion. Add a parent-relay positive only if
   terminal custody/cleanup is touched. Do not repeat the unrelated 142-test
   recovery lane or aggregate suites. Rebuild the smoke assembly, then run
   `CanonicalFallback_RequiresOriginalHandoffsBeyondOldOriginCap` once offline.
   Require original native handoff and primary analysis; retain any new first
   refusal. No retry-until-green or acceptance relaxation.

Expected implementation scope: the pre-connection block in
`tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadWrapper.cs`, plus
directly necessary tests/probes. No changes to native reader/adopter predicates,
coordinator or transport behavior, parser validation, queues, throughput,
payload size, installer, wallet, GUI setup or screen sharing. Preserve the
8192-byte offer bound, existing native-facts bound, two 256 MiB file checks and
all public authority flags. The normal loader must still exclude this adopter.

## Review checkpoint and approval boundary

All 3,968 pins in the prior recovery-buffer checkpoint matched at the current
HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`, with 404 dirty
entries. Its manifest SHA-256 is
`585d947848ca034a44941b93c2cdd409e37fab5ff68b957c920f338b5f9e492f`.
No code or existing report/artifact changed during this review. No tests,
transfer replay, portable build, live network run, GUI matrix, commit or push
were performed. The previously recorded 142 passes and one canonical failure
remain historical results, not new runs.

Evidence and source hashes: `artifacts/listener-lifetime-plan-20260927/observations.json`.
Recovery seal: `artifacts/listener-lifetime-plan-20260927/FINAL-VERIFICATION.json`.

Next approval requested: implement only `payload-listener-lifetime-v1` and its
focused verification gates. Live validation is not yet ready and is not part
of that proposed approval. The second coordinator-published `file_tuna_v4`
remains required by the live route-cycle contract.
