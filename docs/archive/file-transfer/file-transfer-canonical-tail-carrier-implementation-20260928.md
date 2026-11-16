# Canonical tail carrier/controller: checkpoint and publication-shape RCA

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: carrier/controller changes implemented; integration is **not ready for a
full-size run**. One focused regression identifies a test-helper shape blocker.

## Implemented bounded step

The carrier recognizes only the exact `canonical-tail-late-publication` native
case. Mixing it with the old hold/schedule/late flags fails before opening a
carrier or changing the environment. Only the sender owns the localized batch
override; its lifetime encloses runtime/client cleanup. The existing fourth
retained reader receives that optional hook. Both actual contexts must match the
fixed file/chunk geometry before the data release; the original manifest/source
hash must be the canonical SHA. The full-size 180-second deadline, route checks,
original destination hash/native evidence and GUI handoff path are retained.

The controller now translates only the fixed small/canonical geometries:
frontier 12,476, repair 12,476 -> 12,477, highest 12,483 and first write
12,477 -> 12,480. The canonical frame must report 268,369,920 committed bytes
and remain nonterminal/unpaused. Original observation, root, source, repair
receipt, publication owner and physical-copy checks remain in their existing
service-gated path. No observation is reset, fabricated or replaced.

Before the suffix, the controller waits using the original carrier token, not
an eight-second episode timer or a restarted full-size clock. It checks original
currentness and rejects an already closed/source-bound/repair-bound observation;
an unbound observation containing ordinary prefix progress is allowed unchanged.
Once a suffix frame arrives, the existing eight-second edge waits apply. Native
publication timings and per-frame hold expiries are not changed.

The top-level offline dispatcher explicitly refuses the new composite profile
until its same-pair primary integration is complete. This is a pre-launch safety
gate, not the later success sentinel or a changed acceptance rule. Existing
small/full profiles and ordinary/live dispatch remain unchanged.

## Focused verification and first missing edge

The initial new carrier/controller test set passed 52 cases. A follow-up
integration test then exercised the existing publication owner with the exact
canonical frame and failed at:

`AttemptLatePublicationOwner.Bind` -> `AttemptOfflinePublicationDelay` constructor
-> `publication_arm_unavailable`.

The constructor at `Support/AttemptOfflinePublicationDelay.cs:34` still requires
`ContiguousCommittedChunkIndex == 4` and `DurableReceivedHighestChunkIndex == 7`.
Those small-profile literals cannot admit the approved canonical values 12,480
and 12,483, regardless of correct native timing. The controller's tail translation
therefore cannot reach publication-delay admission through the unchanged owner.
This is the first newly demonstrated missing edge, not a live NKN failure or a
reason to tune timing, queues, chunk size, retries, throughput or acceptance.

The approved design said to use the publication owner unchanged. Extending its
constructor/forwarding contract needs an explicit clarification of that boundary;
the owner and delay helper were not edited in this checkpoint. Their SHA256s are:

- `AttemptLatePublicationOwner.cs`:
  `a73f8a424124bbb1cc868650706e56edb55b66dcbe18866852c916b1ed9345ee`
- `AttemptOfflinePublicationDelay.cs`:
  `d050d97229eae14b9f78203a3a82a5828455c518db625f3baa2498bdb18725b2`

Results retained under `artifacts/canonical-tail-carrier-implementation-20260928/`:

- `canonical-carrier.trx`: 52 passed.
- `canonical-carrier-owner.trx`: two cancellation checks passed; the newly added
  exact-tail publication-owner regression failed as described above.
- `canonical-carrier-owner-confirmation.trx`: the same admission failure remains
  on the final build. Review corrected the test's later completeness assertion
  to require `publication_physical_completion_missing`; a zero pending-copy count
  alone cannot prove that any of the three physical writes occurred. That later
  assertion is not yet reached because admission fails first.
- `existing-profile-owner.trx`: 26 existing admission, dispatch-exclusion and
  publication-owner regressions passed once. The cancellation checks overlap the
  first run, so the unique total is **78 passing, one failing**.
- The dispatcher parses successfully. The carrier's Windows platform warnings
  match the previous build after line-number normalization; no new warning kind
  was introduced there. No warnings were emitted for the new controller tests
  or modified controller helper.

Tests used the normal sandbox, Debug, `--no-restore`, and
`-p:UsedAvaloniaProducts=`. Logging variables were process-local and redirected
to the checkpoint directory. Tests exercise value guards, original-token waiting
(including a prefix longer than eight seconds), and the existing helper's
admission boundary. They do not establish a native receipt, full-size custody,
actual transfer completion, witness ownership or the full-input work budget.
The failing regression is intentionally retained rather than skipped or weakened.

## Proposed minimal fix, pending the requested contract clarification

1. Pass an explicit default-off canonical-tail flag from the named controller
   profile through the test-only publication owner to its existing delay helper.
   Do not infer a profile from an arbitrary frame or add configurable offsets.
2. Keep the default helper's 4/7 restriction unchanged. Only the explicit
   canonical flag may admit exact 12,480/12,483, the fixed committed byte count,
   and the existing nonterminal/unpaused state. Preserve all native ownership,
   source/identity, copy-count, capacity, timeout, expiry and cleanup checks.
3. Make the failing test pass and add negatives for canonical geometry without
   the flag, small geometry with the canonical flag, wrong tail geometry/bytes,
   and unchanged timing/ownership rejection. Rerun only those affected tests and
   their directly affected helper lane once after stabilization.
4. Complete the still-pending actual-pair GUI/wrapper/parent handoffs and combined
   original primary witness worker. Only after focused checks and review should
   the pre-launch gate be replaced by the exact composite admission/success
   sentinel and the one-shot full-size offline run be separately gated.

No full-size transfer, live-256 run, GUI matrix, commit or push occurred. No
production source, publication-owner policy, acceptance rule or limit was
changed. All prior work and before-images are preserved; continue from this
checkpoint and its failing test rather than rerunning older native captures.
