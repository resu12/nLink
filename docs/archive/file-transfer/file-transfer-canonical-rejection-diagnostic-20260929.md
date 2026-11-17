# Canonical readiness rejection: bounded diagnostic

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: **implemented; 39 focused checks pass**. No new native transfer ran.

## Scope and evidence

The preceding offline attempt failed at the sparse-shape readiness check before
primary analysis. Its last saved commit is not the exact rejected snapshot, so
pending writes remain a hypothesis, not an established cause. The failed capture
and its report, `file-transfer-canonical-prefix-native-characterization-20260929.md`,
remain unchanged. This step fills that diagnostic gap; it does not relax the guard.

`AttemptOfflineLatePublicationProgress.ReadSparseShape` calls the unchanged
`MatchesSparseShape` predicate. Only a canonical-profile rejection captures:

- Exact `next` and `highest` values used by the predicate.
- Pending-write count and whether the pending set contains `next`.
- Written-bit array length and the bit at `next`, with `-1` for unavailable or
  out-of-bounds data rather than a misleading false value.
- Observation presence, closed state, bounded status, and source/repair binding
  booleans. No identities, payloads, pending-set enumeration or authority tokens
  are retained.

Capture happens inside the existing service lock, after the existing currentness,
geometry and unbound-observation checks. Only a scalar string leaves that lock.
The outer failure handler attempts one console line prefixed
`carrier-frontier-sparse-shape-rejected`; it clears that string before writing,
so failed output is not retried. Console I/O is outside the service lock.

Status is limited to 64 ASCII letters/digits or replacement underscores. Output
is a single line under 512 characters. A diagnostic read failure retains the exact
indices with `snapshot=unavailable`; diagnostic read/output failures cannot replace
the original readiness exception. The original rethrow, outer stage classification
and cleanup path are retained. Successful/not-ready checks do not read diagnostic
fields, and the small profile remains uninstrumented.

## Verification

Artifacts: `artifacts/canonical-rejection-diagnostic-20260929/`.

- Red: 11 diagnostic checks failed with a pass-through wrapper and no capture;
  nine unchanged-behavior/member-shape controls passed.
- Green: all 20 new checks pass. Coverage includes snapshot immutability after the
  read, exact values, one-time output, original exception/stack, diagnostic failure,
  missing/empty/out-of-range written bits, large status/pending input, observation
  flags, profile scope and reflection-member compatibility with the actual Core types.
- Direct regression: all 19 existing exact-tail/prefix/rejection and unbound/bound
  observation checks pass. Unchanged timing, composition, analyzer and aggregate
  lanes were not rerun.

These are deterministic value-shape tests, not native witnesses. Review of the
carrier's output checks found that the added rejection prefix does not overlap
its existing success markers. The prior readiness predicate, wait loops, hold
policy, currentness/observation guards and production sources are unchanged.

Entry matched 4,322 pinned hashes and 555 dirty entries. Before-images preserve
the edited helper and five pinned build binaries. Only the helper and rebuilt
test DLL changed among prior pins; all four pinned Core/Infra DLL copies remain
unchanged. The new test file and this report are the only new non-ignored files.
Existing accumulated work and prior failed-run evidence are preserved.

## Next gate

Rebuild and freeze the diagnostic source/binary hashes, verify strict escalated
preflights, then perform one separately gated offline canonical-tail attempt with
a fresh create-new marker. Preserve the consumed previous runners and captures.
If rejection recurs, require the exact new snapshot before proposing another
readiness change; `snapshot=unavailable` is still missing evidence, not permission
to broaden the predicate.

Keep the 600-second outer, 180-second carrier, eight-second edge, four-second
held-frame and 20,000 ms native-primary work limits unchanged. Stop at the first
remaining edge; no automatic retry. No runtime/authority/acceptance change, native
transfer, live-256 run, full GUI matrix, commit or push occurred in this step.
