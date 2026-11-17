# Initial fallback startup: RCA and bounded fix contract

## RCA checkpoint status

RCA and fresh offline reproduction complete on `v0.7.0`, HEAD
`0a2cd9d37dbd81149e367f6ce8729d0e0f7e82a0`. No production or analyzer
acceptance change is implemented here. The new tests characterize the failure
and require the analyzer to remain fail-closed; their passing result is not a
fixed route contract or live acceptance result.

The subsequent binding-prerequisite implementation is recorded separately in
`file-transfer-initial-fallback-binding-20260923.md`; the checkpoint observations
and artifacts below remain historical evidence, not a claim about the new code.

## Current authoritative live result

Use only `artifacts/window-close-restart-live-20260923-0a2cd9d3` for the live
selection. Its verification manifest SHA-256 is
`0471bdfb37704eb097efd71b99536a973827e8c0d5327bea33aec83f928432c6`.
The original parent analysis completed with a valid receipt and full/compact
parity, but route consistency failed. The overall targeted scenario remains
`FAIL/correctness/incomplete_proof`; a matching second payload hash does not
prove the required second `file_tuna_v4` publication.

The parent-selected boundary is `fte1-6ea5a3136b1797b4502e25b3`, hash
`f4d0462301b3cf4208079fdbe28e97f65c1d202007e7b47528882701e42c96e8`,
at retained source line 34567, writer `p32012r4b3b4c836f724d0e`, sequence
15066. The retained input has 39,972 lines and SHA-256
`ae07baec061462c4f8dee042a78c7fcc8974502959deafdcc21077e2edf8e2f4`.
Only the selected line was decoded for source inspection; the historical
authority analysis was not rerun.

That snapshot records an accepted inbound `StartTransfer`, reason
`post_tuna_fallback_v6_route`, aggregate generation 7 to 8. Route/protocol stay
`post_tuna_fallback_v6`/6 at live epoch 0, with data-session generation 1.
Runtime, leg, and publication generations advance from 1 to 2. Exactly one
old owner and publication retire and one successor of each is installed. No
route-transition operation is present. The reported finding is:

`snapshot same-route publication has no exact fallback owner successor contract`

This selection does not by itself establish how the earlier live data session
was attached. The attachment gap below is proved by the fresh service fixture
and current source, not by another selection over historical live evidence.

## Source trace and root cause

1. Initial offer/send admission creates a coordinator owner and initial route
   publication before the data session is attached.
2. `ReplaceOutboundDataSessionLocked` / `ReplaceInboundDataSessionLocked` in
   `SessionFileTransferService.PullTransferSession.cs` directly assign the
   session and advance `DataSessionGeneration`. They invalidate relevant
   existing operations, but do not emit a coordinator transition for the first
   attachment itself.
3. The V6 sender startup and inbound manifest handler in
   `SessionFileTransferService.PullTransferSessionV6.cs` call the existing
   post-Tuna recovery startup using `post_tuna_fallback_v6_route`.
4. The recovery helper deliberately starts a checkpoint-pending leg with data
   send disabled. `FileTransferCoordinator.StartTransfer` replaces the runtime
   owner, and `ForwardFallbackRoutePublicationAuthorityAfterLegMutation`
   authorizes the new fallback publication. The service retires old-owner work
   and emits the publication-forwarding effect. This is not an adapter-issued
   route publication or evidence that the helper should simply be removed.
5. `Test-FileTransferCoordinatorSnapshotFallbackOwnerPublication` recognizes
   only the different `state_refresh_received` successor path, which requires
   an earlier committed Tuna-to-fallback transition and delivered bootstrap.
   Initial fallback startup has neither. The analyzer therefore rejects it.

There is a prerequisite to adding that missing startup contract: in a fresh
real-service loopback transfer, the initial publication ends at aggregate
generation 2 with data session `(none)`/0. Startup begins at the same aggregate
generation and cached hash with a real identity/generation 1. No intermediate
binding transition explains the changed boundary tuple. The existing snapshot
builder uses the retained `SnapshotStateFingerprint` as its before hash; hash
equality alone does not prove the missing scope transition.

Consequently, broadening the reason check or dropping the previous-transition
requirement alone is insufficient. Allowing arbitrary data-session changes in
the interval reducer would also hide a real evidence gap.

## Fresh verification

`FileTransferInitialFallbackStartupEvidenceTests` uses ordinary service send,
offer acceptance, manifest handling, loopback transport, and production snapshot
emission. It does not seed coordinator ownership, fabricate positive snapshots,
or replay live captures. The 128,000-byte transfer completes on both sides and
the received bytes match exactly.

Artifacts: `artifacts/initial-fallback-startup-20260923`.

- Desired route-pass assertion: two expected failures, one per PowerShell host,
  retained in `red/initial-fallback-startup-red.trx`; both reproduce exactly the
  two same-route successor findings.
- Final fail-closed characterization: 2/2 pass in
  `characterization/initial-fallback-startup-characterization.trx`.
- Windows PowerShell and PowerShell 7 agree: artifact evidence and stream
  completeness pass; snapshot and route parity pass; route verdict remains
  fail. Both directions have an initial owner proof, an unwitnessed first
  attachment, and an exact retired/new owner-publication pair. Four actual
  publication-forwarding effects are retained.
- One follow-up build attempt hit restricted access to Avalonia's external
  telemetry log. Rebuilding with the command-local `UsedAvaloniaProducts=`
  property disabled that telemetry target. No source/package setting changed;
  this was not a network or application failure.

## Bounded implementation contract

Implement the prerequisite before admitting the successor. Preserve all
existing checkpoint-refresh, route-transition, and stale-callback guards.

### 1. Coordinator-owned first fallback data-session attachment

- Add a typed initial-attachment request/decision, bound to the exact current
  runtime owner and publication and the session/transfer/direction identities.
- Limit the first implementation to nonterminal initial fallback protocol 6,
  live epoch 0, first publication, no existing data-session identity/generation,
  and no active route transition, checkpoint, recovery, or retry scope requiring
  its separate invalidation contract. Later replacements are not an exemption.
- The coordinator accepts only the first attachment, generation 0 to 1, and
  returns the exact target binding. Session observations alone grant no route,
  runtime-unlock, retry, repair, or publication authority.
- Record the before/after scope and aggregate generation/hash transition using
  a typed binding witness. Preserve the current route, owner, leg, publication,
  and all unrelated authority identities. No second publication is issued by
  the binding itself.
- The service applies the exact accepted binding under its existing lock before
  starting callbacks/pumps. Stale, foreign, duplicate/replacement, terminal, or
  rejected requests must not mutate the context or trigger downstream work.

### 2. Exact initial-fallback startup successor evidence

- Require the validated initial publication/owner proof, then a complete
  aggregate chain including the exact first-attachment witness. No inferred
  binding based only on a changed generation, log message, or cached hash.
- Admit only accepted `StartTransfer` / `post_tuna_fallback_v6_route` for that
  initial fallback scope, publication 1 to 2, with unchanged bound data session,
  epoch, protocol, and unrelated scope. Runtime and leg each advance exactly
  once; their generation domains must not be conflated with publication.
- Require unique old-token retirements, unique new owner/publication entrants,
  immutable old token identities/fingerprints, the exact attempted command,
  validated publication receipt, and exactly one bound forwarding effect.
- Require no fabricated prior Tuna transition/bootstrap. Preserve the existing
  stricter transitioned-fallback checkpoint-refresh path unchanged.
- Missing binding/history, substituted receipts, duplicate effects, stale
  owners, unexplained scope changes, later publications, and wrong route or
  protocol must remain rejected in both full and compact consumers.

### 3. Verification and later live gate

First add focused Core rejection/acceptance and service application tests for
the binding. Then update this fresh loopback fixture to require route pass,
adding negative controls for every evidence guard above. Verify directly
affected initial-route and checkpoint-successor tests once; do not run aggregate
or unrelated GUI lanes repeatedly.

Only after those contracts pass: review the bounded diff, make the appropriate
local checkpoint, rebuild the portable package, run strict preflights outside
the restricted sandbox, and rerun only `second-transfer-after-reactivation`.
Let its single parent-owned analysis select any next live edge. No full matrix,
throughput tuning, historical re-analysis, timeout relaxation, or push belongs
to this RCA step.
