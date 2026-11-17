# Pressure-settlement generation domains: implementation result

Implemented against `f107b5a1f6519e1c65be3eb3ff6af02b6e0fe120` on `v0.7.0`.
Changes are local and uncommitted. No portable rebuild, live GUI/NKN run, commit,
push, or historical authority re-evaluation was performed.

## Bounded change

- The retained-predecessor snapshot selector now checks installed command and
  validated receipt generations against the pressure-install generation. It
  separately binds operation generation to the authority's decision generation.
- The validator compares attempted operation/decision/command/receipt generations
  within the repair domain and checks predecessor-to-successor repair ordering.
  Settlement receipt and payload install generations remain exactly bound to
  the attempted payload's install generation.
- Exact successor/predecessor identities, source/effective boundaries, receipt
  fingerprint binding, unique attempt selection, terminal mirrors, predecessor
  retirement, and the prohibition on successor mutation remain intact.
- No coordinator decision algorithm, route token, effect-binding implementation,
  schema/catalog, transport, GUI, timeout, retry, or performance setting changed.
  Production changes are confined to the snapshot selector and the existing
  pressure-settlement validator branch.

## Regressions and results

The existing successor-retirement test is shared with the new analyzer tests
through a fixture helper. Fixture history seeds independent counters before
coordinator issuance; repair authorities and pressure bindings still come from
the real coordinator APIs, not fabricated tokens. It covers regular-v4 and
Tuna-v4, inbound and outbound, for both equal counters and the live-shaped
repair 17/18 versus install 7/8 case. Existing current-clear, wrong-scope,
idempotent, stale-receipt, and exact effect-binding assertions are retained.

1. **Producer red:** 4 divergent cases failed at the missing attempted witness;
   4 equal-domain cases passed before the producer fix.
2. **Validator red:** after the producer correction, PowerShell 7 rejected the
   coordinator-produced divergent settlement with the expected binding failure.
   The first Windows PowerShell run instead exposed a new test-harness array
   wrapping bug; it is not counted as a validator red result.
3. **Focused coordinator green:** all 8 producer cases passed. The initial
   combined run still had the Windows PowerShell harness failure, subsequently
   fixed by unwrapping the JSON array correctly and asserting eight fixtures.
4. **Final focused analyzer green:** both Windows PowerShell 5.1 and PowerShell 7
   passed **168 validator controls per host**, plus operation-catalog checks,
   across eight coordinator-produced envelopes. Controls include unequal/equal
   counters, non-adjacent ordered repair generations, missing/duplicate attempt,
   wrong identities, mismatched or zero install counters, wrong repair/attempt
   counters, invalid ordering, fingerprint/boundary mismatches, successor mutation,
   missing mirror, and missing predecessor retirement. The non-adjacent case is
   a direct validator fixture, not a claimed runtime history.
5. **Affected lanes:** 31 other pressure-coordinator cases and 6 existing
   file-Tuna settlement analyzer cases passed. The latter exercise the shared
   full/compact evaluator and offline GUI classifier, including cross-route
   terminal rollover and initial-Tuna settlement. These are not live GUI tests.

Final result: **47 unique passing tests**, zero unresolved failures. Successful
latest results supersede the intentional red runs and corrected harness failure.
The affected lane excluded the eight already-run successor cases; no aggregate
contract/analyzer suite was run. After adding the catalog check, only the two
directly affected analyzer-host tests were rerun. Existing build warnings were
not changed as part of this fix.

## Verification and remaining gate

Artifacts are under
`artifacts/pressure-settlement-generation-implementation-20260924`.
`Verify-Implementation.ps1` verifies red/green TRX results, the 336 total validator
controls, unchanged historical evidence, the bounded source diff, and file hashes.
It does not execute the authority evaluator on the historical live log.

The old live campaign remains FAIL / incomplete proof. This patch corrects its
first missing proof edge; it does not establish that the later second-transfer
publication timeout is resolved. The exact retoggle branch also still lacks live
coverage.

Next: pre-commit review/checkpoint, fresh portable build, then only the targeted
`second-transfer-after-reactivation` run with strict outside-sandbox preflights
and no implicit retries. Require the second coordinator-published Tuna-v4 route
plus terminal and integrity checks. Full matrix remains gated.
