# Canonical prefix barrier: first native capture RCA and bounded fix plan

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: **one native capture failed during sender fixture setup; no retry or
runtime/test-code change made in this step**.

## Result and first missing edge

After a successful Debug rebuild and two passing outside-sandbox preflights, the
single offline native 256 MiB capture failed before payload release:

```
AttemptCanonicalPrefixScope.Check():61
  IOException: canonical_prefix_scope_retired
AttemptCanonicalPrefixScope..ctor():40
RunCrossProcessCarrierAsync():321
```

The receiver subsequently canceled while connecting the dedicated prefix pipe
(`AttemptCanonicalPrefixHandoff.OpenAsync:67`). The parent reported cancellation
and incomplete writer shutdown. These do not establish a later transfer failure:
the first observed blocker is construction of the sender prefix scope, before
pipe establishment and the release at carrier fixture line 343.

Test:
`NLink.SmokeTests.FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`.
TRX outcome: failed, duration 211.0429828 seconds. The TRX start/end attributes are
nearly identical and are not used to infer elapsed time.

Prefix exports, prefix consumes, pre-arms, described tails, hold-timing records,
receiver stages and same-pair completions were all **zero**. The original
four-second physical-receipt hold never began. This capture neither validates the
barrier nor adds evidence about the remaining tail timing budget.

This was the offline native-process fixture, not live NKN or desktop GUI. It
consumes the rebuilt Debug test/application output; no portable GUI package or
full GUI matrix was run.

## Evidence and confidence

Fresh fixture directory:

`artifacts/canonical-tail-native-characterization-20260929/pwsh-044126116d9f46e28164d7b32b2a8cff/`

Sender PID 39944, writer `p39944r05fe61037cdc441a`; receiver PID 37368.
In its `live-operational-log/nlink.log`, sender events show:

| File line / writer sequence | Observation |
| --- | --- |
| 897 / 492 | Accepted fallback-v6 route publication, runtime generation 3, terminal generation 0, data-session generation 1. |
| 960 / 506 | Accepted receiver-state diagnostic: epoch 4, committed bytes 0, `transfer_paused=1`. |
| 966 / 511 | Tuna-v4 to fallback-v6 transition committed using `filetransfer.fallback_bootstrap_state.v6` proof. |
| 975 / 520 | Fallback-v6 live route recovered, live epoch 2, runtime generation 3, terminal generation 0. |

Relevant source anchors:

- `src/nLink.Core/FileTransfer/SessionFileTransferService.FallbackV6ProofBootstrap.cs:550`
  deliberately builds a paused bootstrap state; line 551 identifies recovery mode.
- `src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV6.cs:536`
  assigns accepted receiver-state pause to the sender context's `PeerPaused`;
  line 730 emits the receiver-state diagnostic.
- `tests/nLink.SmokeTests.Core/Support/AttemptCanonicalPrefixScope.cs:55`
  rejects either user or peer pause in its general currentness check. The constructor
  invokes this check at line 40 for both roles, before pipe setup.
- `tests/nLink.SmokeTests.Core/AttemptCanonicalPrefixScopeTests.cs:22` and line 42
  create an already-ready inbound context, not the outbound bootstrap-paused setup
  state encountered here.

The source and fresh paused bootstrap observation identify a concrete sufficient
rejection path: the new test guard conflates setup-time sender peer pause with
retirement. This is the leading fixture RCA, not proof that production route
authority is defective. The compound exception does not record all predicates
atomically, so it cannot establish that pause was the sole failing predicate at
the throw. Data-session generation zero is not supported by this capture (the
recorded generation is 1). Later worker/terminal diagnostics are retained but are
not promoted into a second RCA without their own causal proof.

## Bounded next step

Review and test a role/phase-specific distinction between retained scope identity
and actual committed-prefix readiness before changing the guard:

1. Reproduce an otherwise-current **outbound bootstrap-paused** context and the
   constructor failure in a focused regression. Keep current inbound pause,
   retirement, token replacement and cancellation rejection controls. Add a narrow
   diagnostic if necessary to distinguish failed predicates without granting
   authority or changing their decisions.
2. Define which setup checks may establish the dedicated pipe without asserting
   readiness. Preserve original context/data-session/lifetime, retained peer,
   coordinator owner/publication and geometry checks throughout. Setup completion
   must not consume a witness, open the tail gate, release authority or manufacture
   unpause. The exact receiver prefix predicate must remain nonpaused.
3. Trace independent ordinary feedback through setup and the sender's checks at
   message consumption/first tail read. Do not simply wait for unpause before
   connecting the pipe: that ordering must first be shown not to block the
   feedback needed to make progress. Explicitly review sender pause handling at
   consumption; no silent broad removal of `PeerPaused` checks.
4. Implement only the reviewed test-fixture correction and directly affected
   regressions. Prove that pause cannot masquerade as committed-prefix readiness,
   retirement still cancels pending work, and pipe setup plus ordinary prefix
   feedback can progress without held service/carrier/scheduler locks.
5. After focused checks stabilize, run the directly affected canonical scope,
   handoff and retained-reader lane once. Rebuild, review preservation/preflights,
   then perform a separately gated single native capture. No automatic retry or
   full matrix.

Success requires advancing past scope/pipe setup without weakening the exact
receiver witness, first-tail/cache-bypass guards or original lifetime. A later
missing edge is a new result, not permission to extend a deadline.

No Core/Infra bootstrap or pause behavior, route authority, wire protocol, timer,
hold start/expiry, acceptance rule, queue/concurrency or chunk geometry should be
changed for this fixture failure. Do not clear pause flags, send synthetic resume
messages, use pause-reason strings as authority, or create a new timeout.

## Verification and preservation

- Debug rebuild passed with zero warnings/errors. Core, Infra and test DLL hashes
  matched the frozen inputs.
- Both native preflights passed outside the restricted sandbox. The run retained
  original carrier/outer/analyzer lifetimes and the 20,000 ms native work budget.
- Saved evidence for 243 distinct prefix-barrier/regression checks was verified,
  not rerun. No new unit-test pass is claimed for this capture.
- Read-only cleanup verification at 05:59:00 UTC found no native/test peers; the
  256 MiB source accepted an exclusive read. No remaining destination partial,
  retained analysis or same-pair observation was found. No user files were deleted.
- Post-run preservation at 06:01:57 UTC matched all 4,513 frozen pins and all
  569 pre-existing dirty entries. This report is the only added nonignored file.
  No commit or push was made.

Preflights, rebuild log, failed TRX, selected writer-correlated observations and
cleanup evidence are under `artifacts/canonical-prefix-barrier-native-20260929/`.
The final seal/check in that directory records the new report and retained run
artifacts; the original implementation report and contract remain unchanged.

Recommended next step: the bounded setup-versus-readiness guard review and focused
regression above. **No changed pause contract or retry is approved by this report.**
